<#
.SYNOPSIS
  Fails (exit 1) when any file under -Path contains a database credential, a JWT or a private key, in UTF-8/ASCII or UTF-16LE.

.DESCRIPTION
  Used by CI and by the release and deploy workflows on every built artifact: publish folders, installers, update
  packages, container image filesystems and logs. Zip-based files (zip, nupkg, vsix, msix, jar, ...) are opened and
  every entry is scanned, nested up to 3 levels, so an installer package cannot hide a credential by compressing it.

  What counts as a hit (each is reported by rule name, file, encoding and byte offset; the matched value is never printed):
    PasswordValue   the Password or Pwd connection-string keyword, an equals sign, then a value
    UserIdValue     the "User ID" keyword (any case, with a space) or the exact synonym UserID, an equals sign, then a value;
                    identifier-style userId (library messages, code) is not a keyword
    SqlHostCreds    the Azure SQL host name followed (within 400 characters, same line) by one of those keywords or UID and a value,
                    or credentials in a URL to that host (user:secret at host)
    Jwt             three dot-separated base64url parts starting eyJ.eyJ
    PrivateKey      a PEM "-----BEGIN ... PRIVATE KEY-----" header followed by key material (at least 16 base64 characters)
  A value is not counted when it is empty or a placeholder: starts with { $ % < * (format strings, ${VAR}, $variable, %VAR%).

  Exit codes: 0 clean; 1 at least one finding; 2 nothing was scanned (path missing, no files, or a read error), because a
  scan that looked at nothing proves nothing.

.PARAMETER Path        One or more files or folders.
.PARAMETER ReportPath  Optional JSON report: files scanned, bytes, findings (no values).
.PARAMETER MaxArchiveDepth  How deep to open archives inside archives (default 3).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string[]]$Path,
    [string]$ReportPath,
    [int]$MaxArchiveDepth = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$ChunkSize = 4MB
$Overlap = 1024                      # longest span any rule needs to see across a chunk boundary
$MaxEntryBytes = 512MB
$MaxFindingsPerFile = 20
$archiveExtensions = '.zip', '.nupkg', '.vsix', '.msix', '.appx', '.jar', '.whl', '.vpk'

# A value is made of printable ASCII and Latin-1 letters only. In a UTF-16 string table (compiled .NET code) the literal
# "Password = " is followed by the next table entry, whose first UTF-16 unit is U+0100 or higher, so a record that merely has a
# Password property is not a hit, while real UTF-16 credentials (ASCII and Latin-1 values) still are.
$printable = '\x21\x23-\x26\x28-\x2B\x2D-\x3A\x3D\x3F-\x7E\xA1-\xFF'   # printable minus the delimiters  "  '  ,  ;  <  >
# The only values that are not credentials are complete placeholders: {0} {name} ${NAME} $(NAME) %NAME% <your password>, followed by
# something that cannot continue a value. A value that merely STARTS with { $ % < * (a real password can) is a hit.
$placeholder = '(?:\{[\w.:,\-]{0,40}\}|\$\{[^}\s;"'']{1,60}\}|\$\([^)\s;"'']{1,60}\)|%[A-Za-z_]\w{0,60}%|<[A-Za-z][\w \-]{0,40}>)(?!\w)'
$value = '(?:"(?!' + $placeholder + ')[\x20\x21\x23-\x7E\xA1-\xFF]{1,200}"|''(?!' + $placeholder + ')[\x20-\x26\x28-\x7E\xA1-\xFF]{1,200}''|(?!' + $placeholder + ')[' + $printable + '<][' + $printable + ']{0,199})'
$ci = [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant
# A keyword is a whole word: it must not follow a word character, $ @ % . or - (so perl's $pwd, $newpwd, the help key id-help-password
# and a dotted property such as request.Password are not keywords).
$notAfter = '(?<![\w$@%.\-])'
$rules = @(
    @{ Name = 'PasswordValue'; Regex = [regex]::new($notAfter + '(?:password|pwd)\s*=\s*' + $value, $ci) },
    # Prefixed keys (DbPassword=..., MSSQL_SA_PASSWORD=..., AdminPwd=...) in the tight key=value form only, so C# "NewPassword = x;" and
    # Configure's i_pwd='define' are not hits.
    @{ Name = 'PasswordValue'; Regex = [regex]::new($notAfter + '(?:\w+password|(?:db|sql|sa|admin|app)_?pwd)=' + $value, $ci) },
    # "User ID" (any case, with a space) and the exact synonym "UserID". Identifier-style userId=value (library messages, code) is not a keyword.
    @{ Name = 'UserIdValue'; Regex = [regex]::new($notAfter + '(?:(?i:user\s+id)|UserID)\s*=\s*' + $value, [Text.RegularExpressions.RegexOptions]::CultureInvariant) },
    @{ Name = 'SqlHostCreds'; Regex = [regex]::new('database\.windows\.net[^\r\n]{0,400}?' + $notAfter + '(?:password|pwd|user\s*id|uid)\s*=\s*' + $value, $ci) },
    @{ Name = 'SqlHostCreds'; Regex = [regex]::new('://[^/\s:@]{1,100}:[^/\s@]{1,200}@[^\s/]*database\.windows\.net', $ci) },
    @{ Name = 'Jwt'; Regex = [regex]::new('\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{10,}', [Text.RegularExpressions.RegexOptions]::CultureInvariant) },
    @{ Name = 'PrivateKey'; Regex = [regex]::new('-----BEGIN [A-Z ]{0,30}PRIVATE KEY-----\s*[A-Za-z0-9+/=]{16}', [Text.RegularExpressions.RegexOptions]::CultureInvariant) }
)

$script:findings = [System.Collections.Generic.List[object]]::new()
$script:filesScanned = 0
$script:bytesScanned = 0L
$script:errors = [System.Collections.Generic.List[string]]::new()
$latin1 = [Text.Encoding]::Latin1
$utf16 = [Text.Encoding]::Unicode

function Test-Buffer {
    param([byte[]]$Buffer, [int]$Length, [long]$Base, [string]$Label, [System.Collections.Generic.HashSet[string]]$Seen, [ref]$Count)
    # Each view is (name, string, offset multiplier, byte shift). Latin-1 maps every byte to one char, so ASCII/UTF-8
    # patterns are found at any offset; UTF-16LE needs both byte alignments because a string may start on an odd byte.
    $views = [System.Collections.Generic.List[object]]::new()
    $views.Add(@{ Enc = 'UTF-8'; Text = $latin1.GetString($Buffer, 0, $Length); Step = 1; Shift = 0 })
    foreach ($shift in 0, 1) {
        $n = (($Length - $shift) -shr 1) * 2
        if ($n -ge 2) { $views.Add(@{ Enc = 'UTF-16LE'; Text = $utf16.GetString($Buffer, $shift, $n); Step = 2; Shift = $shift }) }
    }
    foreach ($view in $views) {
        foreach ($rule in $rules) {
            foreach ($m in $rule.Regex.Matches($view.Text)) {
                $offset = $Base + $view.Shift + $m.Index * $view.Step
                $key = '{0}|{1}|{2}' -f $rule.Name, $view.Enc, $offset
                if (-not $Seen.Add($key)) { continue }
                $Count.Value++
                if ($Count.Value -le $MaxFindingsPerFile) {
                    $script:findings.Add([pscustomobject]@{ file = $Label; rule = $rule.Name; encoding = $view.Enc; offset = $offset })
                }
            }
        }
    }
}

function Test-Stream {
    param([IO.Stream]$Stream, [string]$Label, [int]$Depth)
    $buffer = New-Object byte[] ($ChunkSize + $Overlap)
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $count = 0
    $carry = 0
    $base = 0L
    $first = $true
    while ($true) {
        $read = 0
        while ($read -lt $ChunkSize) {
            $r = $Stream.Read($buffer, $carry + $read, $ChunkSize - $read)
            if ($r -le 0) { break }
            $read += $r
        }
        if ($read -eq 0) { break }
        $script:bytesScanned += $read
        $length = $carry + $read
        if ($first -and $read -ge 4 -and $buffer[0] -eq 0x50 -and $buffer[1] -eq 0x4B -and $buffer[2] -eq 3 -and $buffer[3] -eq 4 -and $Depth -lt $MaxArchiveDepth) {
            Test-Archive -Stream $Stream -Label $Label -Depth $Depth
        }
        $first = $false
        Test-Buffer -Buffer $buffer -Length $length -Base $base -Label $Label -Seen $seen -Count ([ref]$count)
        $keep = [Math]::Min($Overlap, $length)
        [Array]::Copy($buffer, $length - $keep, $buffer, 0, $keep)
        $base += $length - $keep
        $carry = $keep
    }
}

function Test-Archive {
    param([IO.Stream]$Stream, [string]$Label, [int]$Depth)
    if (-not $Stream.CanSeek) { return }
    $position = $Stream.Position
    try {
        $zip = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.Length -eq 0) { continue }
                $entryLabel = "$Label!/$($entry.FullName)"
                if ($entry.Length -gt $MaxEntryBytes) { $script:errors.Add("skipped $entryLabel (larger than $($MaxEntryBytes / 1MB) MB)"); continue }
                $memory = [IO.MemoryStream]::new()
                try {
                    $es = $entry.Open()
                    try { $es.CopyTo($memory) } finally { $es.Dispose() }
                    $memory.Position = 0
                    $script:filesScanned++
                    Test-Stream -Stream $memory -Label $entryLabel -Depth ($Depth + 1)
                } finally { $memory.Dispose() }
            }
        } finally { $zip.Dispose() }
    } catch [IO.InvalidDataException] {
        # Starts with PK\3\4 but is not a readable archive: the raw byte scan still covers it.
    } finally {
        $Stream.Position = $position
    }
}

$files = [System.Collections.Generic.List[IO.FileInfo]]::new()
# `pwsh -File scan-artifacts.ps1 -Path a,b` hands the script one string "a,b": split it when no such path exists.
$expanded = foreach ($p in $Path) { if (Test-Path -LiteralPath $p) { $p } else { $p -split ',' | Where-Object { $_ } } }
foreach ($p in $expanded) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "ERROR: path not found: $p"; exit 2 }
    $item = Get-Item -LiteralPath $p -Force
    if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $p -Recurse -File -Force | ForEach-Object { $files.Add($_) } } else { $files.Add($item) }
}
if ($files.Count -eq 0) { Write-Host "ERROR: no files found under: $($Path -join ', ')"; exit 2 }

foreach ($file in $files) {
    $script:filesScanned++
    try {
        $fs = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { Test-Stream -Stream $fs -Label $file.FullName -Depth 0 } finally { $fs.Dispose() }
    } catch {
        $script:errors.Add("could not read $($file.FullName): $($_.Exception.Message)")
    }
}

if ($ReportPath) {
    [pscustomobject]@{
        paths = @($Path); filesScanned = $script:filesScanned; bytesScanned = $script:bytesScanned
        findings = @($script:findings); errors = @($script:errors)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}

foreach ($f in $script:findings) { Write-Host ("FINDING rule={0} encoding={1} offset={2} file={3}" -f $f.rule, $f.encoding, $f.offset, $f.file) }
foreach ($e in $script:errors) { Write-Host "ERROR: $e" }
Write-Host ("scanned {0} files, {1:N0} bytes: {2} finding(s), {3} error(s)" -f $script:filesScanned, $script:bytesScanned, $script:findings.Count, $script:errors.Count)
if ($script:findings.Count -gt 0) { exit 1 }
if ($script:errors.Count -gt 0) { exit 2 }
exit 0
