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

  Source files (.cs .xaml and similar, scanned from disk, not inside archives) get three precision rules, because ordinary code is full
  of the word Password:
    - a XAML binding or markup extension (Password="{Binding Password}") is not a value;
    - a C# assignment from an identifier or member (Password = string.Empty; Password = _box.Password;) is not a value, when written
      with spaces around the equals sign, outside a comment and outside a string;
    - a literal credential under tests/ (a throw-away test value) is accepted only when tools/secret-scan-allowlist.txt lists it:
      one entry per line, "path|rule|sha256", where path is relative to the repository root and sha256 is the hash of the whole matched
      line (CR/LF trimmed), so editing the line re-flags it. Entries outside tests/ are refused (exit 2). Each source finding prints
      its line number and that hash so a reviewer can add an entry.
  Everything else in source is a finding, including a connection string with a password in a .cs file.

  Exit codes: 0 clean; 1 at least one finding; 2 nothing was scanned (path missing, no files, or a read error), because a
  scan that looked at nothing proves nothing.

.PARAMETER Path        One or more files or folders.
.PARAMETER ReportPath  Optional JSON report: files scanned, bytes, findings (no values).
.PARAMETER Allowlist     Reviewed allow-list of test-only values (default: tools/secret-scan-allowlist.txt next to this script).
.PARAMETER RepoRoot      What the allow-list paths are relative to (default: the folder above tools/).
.PARAMETER MaxArchiveDepth  How deep to open archives inside archives (default 3).
.PARAMETER SkipBuildOutput  When scanning a source tree, skip files under bin, obj and TestResults folders (below each -Path folder), so a local run sees what a CI checkout has.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string[]]$Path,
    [string]$ReportPath,
    [string]$Allowlist,
    [string]$RepoRoot,
    [int]$MaxArchiveDepth = 3,
    [switch]$SkipBuildOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$ChunkSize = 4MB
$Overlap = 1024                      # longest span any rule needs to see across a chunk boundary
$MaxEntryBytes = 512MB
$MaxFindingsPerFile = 20
$archiveExtensions = '.zip', '.nupkg', '.vsix', '.msix', '.appx', '.jar', '.whl', '.vpk'
$csharpExtensions = '.cs', '.csx', '.razor', '.cshtml'
$xamlExtensions = '.xaml', '.axaml'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
if (-not $Allowlist) { $Allowlist = Join-Path $PSScriptRoot 'secret-scan-allowlist.txt' }
# path|rule|sha256 of the matched line. Only tests/ may be listed: a credential in product code is never "test-only".
$allowed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if (Test-Path -LiteralPath $Allowlist) {
    $n = 0
    foreach ($raw in Get-Content -LiteralPath $Allowlist) {
        $n++
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -ne 3 -or $parts[2] -notmatch '^[0-9a-f]{64}$') { Write-Host "ERROR: ${Allowlist}:$n is not path|rule|sha256"; exit 2 }
        $entryPath = $parts[0].Trim().Replace('\', '/')
        if (-not $entryPath.StartsWith('tests/') -or $entryPath.Contains('..')) { Write-Host "ERROR: ${Allowlist}:$n lists $entryPath; only paths under tests/ may be allow-listed"; exit 2 }
        [void]$allowed.Add(('{0}|{1}|{2}' -f $entryPath, $parts[1].Trim(), $parts[2]))
    }
}
$script:allowlistedCount = 0
$sha = [Security.Cryptography.SHA256]::Create()

# Source-only precision. $Value is the text after the keyword's equals sign, $Prefix the line text before the keyword.
function Test-BenignSource {
    param([string]$Ext, [string]$MatchText, [string]$Prefix)
    $eq = $MatchText.IndexOf('=')
    $val = $MatchText.Substring($eq + 1).TrimStart()
    if ($xamlExtensions -contains $Ext) {
        # Password="{Binding Password, Mode=TwoWay}", Foreground="{StaticResource X}", ="{x:Static p:R.X}"
        return $val -match '^"\{[A-Za-z][\w:.]*(?:\s[^"]*)?\}"$'
    }
    if ($csharpExtensions -contains $Ext) {
        # Password = string.Empty;  Password = _box.Password;  (spaces around "=", not in a comment, not inside a string)
        if ($MatchText -notmatch '^[A-Za-z_]+ = \S' -or $Prefix.Contains('//') -or $Prefix.Contains('"')) { return $false }
        return $val -match '^[A-Za-z_@][\w]*(?:\.[A-Za-z_@][\w]*)*(?:\(\))?$'
    }
    return $false
}

# A value is made of printable ASCII and Latin-1 letters only. In a UTF-16 string table (compiled .NET code) the literal
# "Password = " is followed by the next table entry, whose first UTF-16 unit is U+0100 or higher, so a record that merely has a
# Password property is not a hit, while real UTF-16 credentials (ASCII and Latin-1 values) still are.
$printable = '\x21\x23-\x26\x28-\x2B\x2D-\x3A\x3D\x3F-\x7E\xA1-\xFF'   # printable minus the delimiters  "  '  ,  ;  <  >
# The only values that are not credentials are complete placeholders: {0} {name} ${NAME} $(NAME) %NAME% <your password>, followed by
# something that cannot continue a value. A value that merely STARTS with { $ % < * (a real password can) is a hit.
$placeholder = '(?:\{[\w.:,\-]{0,40}\}|\$\{[^}\s;"'']{1,60}\}|\$\([^)\s;"'']{1,60}\)|%[A-Za-z_]\w{0,60}%|<[A-Za-z][\w \-]{0,40}>)(?!\w)'
$value = '(?:"(?!' + $placeholder + ')[\x20\x21\x23-\x7E\xA1-\xFF]{1,200}"|''(?!' + $placeholder + ')[\x20-\x26\x28-\x7E\xA1-\xFF]{1,200}''|(?!' + $placeholder + ')[' + $printable + '<][' + $printable + ']{0,199})'
$ci = [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant
# A keyword is a whole word: it must not follow an ASCII letter, digit or _, or $ @ % . - (so perl's $pwd, $newpwd, the help key
# id-help-password and a dotted property such as request.Password are not keywords). The boundary is ASCII-only ON PURPOSE: in a
# .NET #US string heap every literal is preceded by a length byte, so the UTF-16 unit just before it is (previous terminal byte,
# length byte), often a CJK/Hangul/Balinese letter. A Unicode \w boundary rejected "Pwd=..." literals in real compiled DLLs.
$word = 'A-Za-z0-9_'
$notAfter = '(?<![' + $word + '$@%.\-])'
$rules = @(
    @{ Name = 'PasswordValue'; Regex = [regex]::new($notAfter + '(?:password|pwd)\s*=\s*' + $value, $ci) },
    # Prefixed keys (DbPassword=..., MSSQL_SA_PASSWORD=..., AdminPwd=...) in the tight key=value form only, so C# "NewPassword = x;" and
    # Configure's i_pwd='define' are not hits.
    @{ Name = 'PasswordValue'; Regex = [regex]::new($notAfter + '(?:[' + $word + ']+password|(?:db|sql|sa|admin|app)_?pwd)=' + $value, $ci) },
    # "User ID" (any case, with a space) and the exact synonym "UserID". Identifier-style userId=value (library messages, code) is not a keyword.
    # Source files only: a quoted literal assigned to a member (vm.Password = "x"). The rule above skips dotted names for compiled strings
    # and prose (request.Password), but in code a literal on the right is a hard-coded credential.
    @{ Name = 'PasswordValue'; SourceOnly = $true; Regex = [regex]::new('(?<=[' + $word + '\])]\.)(?:password|pwd)\s*=\s*"(?!' + $placeholder + ')[\x20\x21\x23-\x7E\xA1-\xFF]{1,200}"', $ci) },
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
    param([byte[]]$Buffer, [int]$Length, [long]$Base, [string]$Label, [System.Collections.Generic.HashSet[string]]$Seen, [ref]$Count, [string]$SourcePath)
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
            if ($rule.ContainsKey('SourceOnly') -and -not ($SourcePath -and $view.Enc -eq 'UTF-8')) { continue }
            foreach ($m in $rule.Regex.Matches($view.Text)) {
                $offset = $Base + $view.Shift + $m.Index * $view.Step
                $key = '{0}|{1}|{2}' -f $rule.Name, $view.Enc, $offset
                if (-not $Seen.Add($key)) { continue }
                $extra = $null
                if ($SourcePath -and $view.Enc -eq 'UTF-8') {
                    $text = $view.Text
                    $ls = $text.LastIndexOf("`n", [Math]::Max($m.Index - 1, 0)) + 1
                    if ($m.Index -eq 0) { $ls = 0 }
                    $le = $text.IndexOf("`n", $m.Index); if ($le -lt 0) { $le = $text.Length }
                    $lineText = $text.Substring($ls, $le - $ls).TrimEnd("`r")
                    $ext = [IO.Path]::GetExtension($SourcePath).ToLowerInvariant()
                    if ($rule.Name -in 'PasswordValue', 'UserIdValue' -and (Test-BenignSource -Ext $ext -MatchText $m.Value -Prefix $text.Substring($ls, $m.Index - $ls))) { continue }
                    $hash = [Convert]::ToHexString($sha.ComputeHash($latin1.GetBytes($lineText))).ToLowerInvariant()
                    $rel = $SourcePath
                    if ($allowed.Contains(('{0}|{1}|{2}' -f $rel, $rule.Name, $hash))) { $script:allowlistedCount++; continue }
                    $lineNo = if ($Base -eq 0) { 1 + ($text.Substring(0, $ls).Split("`n").Count - 1) } else { 0 }
                    $extra = @{ line = $lineNo; lineSha256 = $hash }
                }
                $Count.Value++
                if ($Count.Value -le $MaxFindingsPerFile) {
                    $f = [pscustomobject]@{ file = $Label; rule = $rule.Name; encoding = $view.Enc; offset = $offset }
                    if ($extra) { $f | Add-Member -NotePropertyMembers $extra }
                    $script:findings.Add($f)
                }
            }
        }
    }
}

function Test-Stream {
    param([IO.Stream]$Stream, [string]$Label, [int]$Depth, [string]$SourcePath)
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
        Test-Buffer -Buffer $buffer -Length $length -Base $base -Label $Label -Seen $seen -Count ([ref]$count) -SourcePath $SourcePath
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
    if ($item.PSIsContainer) {
        $top = $item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        foreach ($f in Get-ChildItem -LiteralPath $p -Recurse -File -Force) {
            if ($SkipBuildOutput) {
                $segments = $f.DirectoryName.Substring([Math]::Min($top.Length, $f.DirectoryName.Length)).Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)
                if ($segments | Where-Object { $_ -in 'bin', 'obj', 'TestResults' }) { continue }
            }
            $files.Add($f)
        }
    } else { $files.Add($item) }
}
if ($files.Count -eq 0) { Write-Host "ERROR: no files found under: $($Path -join ', ')"; exit 2 }

foreach ($file in $files) {
    $script:filesScanned++
    try {
        $fs = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $src = $null
        if (($csharpExtensions + $xamlExtensions) -contains $file.Extension.ToLowerInvariant()) {
            $full = [IO.Path]::GetFullPath($file.FullName)
            $src = if ($full.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $full.Substring($RepoRoot.Length + 1).Replace('\', '/') } else { $full.Replace('\', '/') }
        }
        try { Test-Stream -Stream $fs -Label $file.FullName -Depth 0 -SourcePath $src } finally { $fs.Dispose() }
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

foreach ($f in $script:findings) {
    $tail = if ($f.PSObject.Properties['lineSha256']) { ' line={0} lineSha256={1}' -f $f.line, $f.lineSha256 } else { '' }
    Write-Host ("FINDING rule={0} encoding={1} offset={2} file={3}{4}" -f $f.rule, $f.encoding, $f.offset, $f.file, $tail)
}
foreach ($e in $script:errors) { Write-Host "ERROR: $e" }
Write-Host ("scanned {0} files, {1:N0} bytes: {2} finding(s), {3} error(s), {4} allow-listed" -f $script:filesScanned, $script:bytesScanned, $script:findings.Count, $script:errors.Count, $script:allowlistedCount)
if ($script:findings.Count -gt 0) { exit 1 }
if ($script:errors.Count -gt 0) { exit 2 }
exit 0
