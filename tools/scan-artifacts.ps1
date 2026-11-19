<#
.SYNOPSIS
  Fails (exit 1) when any file under -Path contains a database credential, a JWT or a private key, in UTF-8, UTF-16LE or UTF-16BE.

.DESCRIPTION
  Used by CI and by the release and deploy workflows on every built artifact: publish folders, installers, update packages,
  container image layers, logs and test captures, and on the source tree. The rules live in tools/scan-engine.cs (compiled here
  with Add-Type; no SDK is needed, PowerShell's own .NET runtime compiles it). In short:

  Binary mode, for every file that is not plain text and for EVERY member of an archive (zip, nupkg, msix, an installer with an
  appended zip such as Velopack Setup.exe, tar, docker image layers, gzip), nested up to -MaxArchiveDepth levels:
    a credential keyword ANYWHERE (password, passwd, pwd, user id, uid, accountkey, sharedaccesskey, client_secret; so
    sa_password, MSSQL_SA_PASSWORD, DbPassword are included), in UTF-8 and in UTF-16LE/BE at both byte alignments, followed by
    "=" or ":" and a value, is a finding when the value is not exactly a placeholder and either a connection-string key
    (server, data source, initial catalog, database, user id, uid, address, addr, with "=" or ":") is within 300 bytes, or the
    value has at least 6 characters. Nothing is decided by the byte BEFORE the keyword: in compiled .NET code that byte is a
    string length and can be anything.
  .NET assemblies are also read with System.Reflection.Metadata: user strings, custom-attribute blobs, string constants and
  manifest resources (.resources strings, BAML and other embedded streams); the text rules below run on every string found.
  Text mode, for plain-text files on disk (configs, logs, source) and for strings read out of metadata:
    PasswordValue / UserIdValue / SecretValue  the keyword as a whole word ("Password", "Pwd", "User ID", "UserID", prefixed keys
                    such as DbPassword=, JSON "password": "..."), then "=" and any value that is not a placeholder
    SqlHostCreds    database.windows.net followed by credentials, or user:secret@ in a URL to that host
    Jwt, PrivateKey a JWT (eyJ.eyJ.sig) or a PEM private key header with key material
  Values: quoted, ODBC {brace-quoted}, or a run up to whitespace ; , < > or a quote; any characters (Cyrillic, euro sign ...).
  Placeholders (exactly, the whole value): {0} {name} {{name}} ${NAME} $(NAME) %NAME% <your password> %s %d ***** and XAML
  {Binding ...} {StaticResource ...} {x:Static ...}. A value that only STARTS like one ({abc}!x, %abc%-x, $x, *x) is a value.

  Source files (.cs .xaml and similar, scanned from disk) get three precision rules, because ordinary code is full of the word
  Password: a XAML markup extension (Password="{Binding Password}") is not a value; a C# assignment from an identifier or member
  (Password = string.Empty;) written with spaces around "=" is code, but only where the keyword is in code: inside any string
  literal (regular, verbatim, interpolated, raw, multi-line) or any comment it is a finding; and a literal credential under tests/
  is accepted only when tools/secret-scan-allowlist.txt lists it ("path|rule|sha256 of the matched line", paths under tests/ only,
  anything else exits 2).
  Third-party files: a benign string that the binary rules match (help text, API names) is accepted only when
  tools/secret-scan-binary-allowlist.txt lists it: "file name|rule|sha256 of the matched text", as printed by a finding. File
  names starting with Lifestream or appsettings are refused (exit 2): the product's own files are never "third-party".

  Exit codes: 0 clean; 1 at least one finding, or (with -GitTracked) a tracked file under a bin, obj or TestResults folder;
  2 nothing was scanned (path missing, no files, a read error, a bad allow-list), because a scan that looked at nothing proves nothing.

.PARAMETER Path              One or more files or folders.
.PARAMETER ReportPath        Optional JSON report: files scanned, bytes, findings (no values), errors, timing.
.PARAMETER Allowlist         Reviewed source allow-list (default: tools/secret-scan-allowlist.txt).
.PARAMETER BinaryAllowlist   Reviewed third-party allow-list (default: tools/secret-scan-binary-allowlist.txt).
.PARAMETER RepoRoot          What source allow-list paths and -GitTracked are relative to (default: the folder above tools/).
.PARAMETER MaxArchiveDepth   How deep to open archives inside archives (default 6).
.PARAMETER MaxFindingsPerFile  How many findings to list per file (default 20; every further one still fails the scan).
.PARAMETER GitTracked        Source-tree mode: scan the files git lists under each -Path (tracked, plus untracked files that are
                             not ignored), so ignored build output is never walked while EVERY tracked file is scanned, including
                             any under bin, obj or TestResults; such a tracked file also fails the scan (exit 1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string[]]$Path,
    [string]$ReportPath,
    [string]$Allowlist,
    [string]$BinaryAllowlist,
    [string]$RepoRoot,
    [int]$MaxArchiveDepth = 6,
    [int]$MaxFindingsPerFile = 20,
    [switch]$GitTracked
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$started = [Diagnostics.Stopwatch]::StartNew()

$sourceExtensions = '.cs', '.csx', '.razor', '.cshtml', '.xaml', '.axaml'
$buildOutputSegments = 'bin', 'obj', 'TestResults'
$ruleNames = 'PasswordValue', 'UserIdValue', 'SecretValue', 'SqlHostCreds', 'Jwt', 'PrivateKey'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $Allowlist) { $Allowlist = Join-Path $PSScriptRoot 'secret-scan-allowlist.txt' }
if (-not $BinaryAllowlist) { $BinaryAllowlist = Join-Path $PSScriptRoot 'secret-scan-binary-allowlist.txt' }

# ---- the engine (tools/scan-engine.cs). LIFESTREAM_SCAN_ENGINE_CACHE (a folder) keeps the compiled DLL between runs (tests).
if (-not ('LifestreamScan.Engine' -as [type])) {
    $engineSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'scan-engine.cs'))
    $cacheDir = $env:LIFESTREAM_SCAN_ENGINE_CACHE
    if ($cacheDir) {
        $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($engineSource + $PSVersionTable.PSVersion + [Environment]::Version))).Substring(0, 16)
        $dll = Join-Path $cacheDir "scan-engine-$key.dll"
        if (-not (Test-Path -LiteralPath $dll)) {
            New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
            $tmp = "$dll.$PID.tmp"
            Add-Type -TypeDefinition $engineSource -OutputAssembly $tmp -OutputType Library
            try { Move-Item -LiteralPath $tmp -Destination $dll -ErrorAction Stop } catch { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
        }
        Add-Type -Path $dll
    } else {
        Add-Type -TypeDefinition $engineSource
    }
}
$engine = [LifestreamScan.Engine]::new()
$engine.MaxArchiveDepth = $MaxArchiveDepth
$engine.MaxFindingsPerFile = $MaxFindingsPerFile

# ---- allow-lists
# Source: path|rule|sha256 of the matched line. Only tests/ may be listed: a credential in product code is never "test-only".
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
        [void]$engine.SourceAllow.Add(('{0}|{1}|{2}' -f $entryPath, $parts[1].Trim(), $parts[2]))
    }
}
# Third-party: file name|rule|sha256 of the matched text. Never the product's own files.
if (Test-Path -LiteralPath $BinaryAllowlist) {
    $n = 0
    foreach ($raw in Get-Content -LiteralPath $BinaryAllowlist) {
        $n++
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -ne 3 -or $parts[2] -notmatch '^[0-9a-f]{64}$' -or $parts[1].Trim() -notin $ruleNames) { Write-Host "ERROR: ${BinaryAllowlist}:$n is not file-name|rule|sha256"; exit 2 }
        $name = $parts[0].Trim()
        if (-not $name -or $name -match '[\\/*?:]' -or $name.Contains('..')) { Write-Host "ERROR: ${BinaryAllowlist}:$n '$name' must be a plain file name (no path, no wildcard)"; exit 2 }
        if ($name -match '^(?i)(lifestream|appsettings)') { Write-Host "ERROR: ${BinaryAllowlist}:$n lists $name; the product's own files (Lifestream*, appsettings*) may not be allow-listed"; exit 2 }
        [void]$engine.BinaryAllow.Add(('{0}|{1}|{2}' -f $name.ToLowerInvariant(), $parts[1].Trim(), $parts[2]))
    }
}

# ---- what to scan
function Get-SourceRel([string]$full) {
    if (-not ($sourceExtensions -contains [IO.Path]::GetExtension($full).ToLowerInvariant())) { return $null }
    $full = [IO.Path]::GetFullPath($full)
    if ($full.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $full.Substring($RepoRoot.Length + 1).Replace('\', '/') }
    $full.Replace('\', '/')
}
function Test-BuildOutputPath([string]$rel) {
    $dirs = @($rel.Split([char[]]@('/', '\'), [StringSplitOptions]::RemoveEmptyEntries) | Select-Object -SkipLast 1)
    [bool]($dirs | Where-Object { $buildOutputSegments -contains $_ })
}

if ($GitTracked) { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) }   # git prints file names as UTF-8
$files = [System.Collections.Generic.List[string]]::new()
$trackedBuildOutput = [System.Collections.Generic.List[string]]::new()
# `pwsh -File scan-artifacts.ps1 -Path a,b` hands the script one string "a,b": split it when no such path exists.
$expanded = foreach ($p in $Path) { if (Test-Path -LiteralPath $p) { $p } else { $p -split ',' | Where-Object { $_ } } }
foreach ($p in $expanded) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "ERROR: path not found: $p"; exit 2 }
    $item = Get-Item -LiteralPath $p -Force
    if ($GitTracked) {
        $full = $item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        if ($full -ne $RepoRoot -and -not $full.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Write-Host "ERROR: $p is not inside the repository $RepoRoot"; exit 2 }
        $rel = if ($full -eq $RepoRoot) { '.' } else { $full.Substring($RepoRoot.Length + 1).Replace('\', '/') }
        $tracked = @(& git -C $RepoRoot -c core.quotepath=off ls-files -z --cached -- $rel)
        if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git ls-files failed in $RepoRoot"; exit 2 }
        $others = @(& git -C $RepoRoot -c core.quotepath=off ls-files -z --others --exclude-standard -- $rel)
        if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git ls-files failed in $RepoRoot"; exit 2 }
        foreach ($name in (($tracked -join '') -split "`0" | Where-Object { $_ })) {
            if (Test-BuildOutputPath $name) { $trackedBuildOutput.Add($name) }
            $abs = Join-Path $RepoRoot $name
            if (Test-Path -LiteralPath $abs -PathType Leaf) { $files.Add($abs) }
        }
        foreach ($name in (($others -join '') -split "`0" | Where-Object { $_ })) {
            if (Test-BuildOutputPath $name) { continue }   # untracked build output is never walked
            $abs = Join-Path $RepoRoot $name
            if (Test-Path -LiteralPath $abs -PathType Leaf) { $files.Add($abs) }
        }
    } elseif ($item.PSIsContainer) {
        foreach ($f in Get-ChildItem -LiteralPath $p -Recurse -File -Force) { $files.Add($f.FullName) }
    } else { $files.Add($item.FullName) }
}
if ($files.Count -eq 0) { Write-Host "ERROR: no files found under: $($Path -join ', ')"; exit 2 }

foreach ($file in $files) { $engine.ScanFile($file, (Get-SourceRel $file)) }
$started.Stop()

$findings = @($engine.Findings | ForEach-Object {
    $f = [ordered]@{ file = $_.File; rule = $_.Rule; encoding = $_.Encoding; offset = $_.Offset; matchSha256 = $_.MatchSha256 }
    if ($_.LineSha256) { $f.line = $_.Line; $f.lineSha256 = $_.LineSha256 }
    [pscustomobject]$f
})
if ($ReportPath) {
    [pscustomobject]@{
        paths = @($Path); filesScanned = $engine.FilesScanned; bytesScanned = $engine.BytesScanned; seconds = [Math]::Round($started.Elapsed.TotalSeconds, 1)
        findings = $findings; findingsOverCap = $engine.FindingsOverCap; allowListed = $engine.AllowListed; allowListEntriesUsed = @($engine.AllowUsed | Sort-Object)
        errors = @($engine.Errors); metadataErrors = $engine.MetadataErrors; trackedBuildOutput = @($trackedBuildOutput)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}

foreach ($f in $findings) {
    $tail = if ($f.PSObject.Properties['lineSha256']) { ' line={0} lineSha256={1}' -f $f.line, $f.lineSha256 } else { '' }
    Write-Host ("FINDING rule={0} encoding={1} offset={2} matchSha256={3} file={4}{5}" -f $f.rule, $f.encoding, $f.offset, $f.matchSha256, $f.file, $tail)
}
if ($engine.FindingsOverCap -gt 0) { Write-Host "($($engine.FindingsOverCap) more finding(s) not listed: over $MaxFindingsPerFile per file)" }
foreach ($t in $trackedBuildOutput) { Write-Host "ERROR: tracked build output: $t (bin, obj and TestResults folders must not be in git; run git rm --cached on it)" }
foreach ($e in $engine.Errors) { Write-Host "ERROR: $e" }
Write-Host ("scanned {0} files, {1:N0} bytes: {2} finding(s), {3} error(s), {4} allow-listed, {5:N1} s" -f $engine.FilesScanned, $engine.BytesScanned, ($findings.Count + $engine.FindingsOverCap), $engine.Errors.Count, $engine.AllowListed, $started.Elapsed.TotalSeconds)
if ($findings.Count -gt 0 -or $trackedBuildOutput.Count -gt 0) { exit 1 }
if ($engine.Errors.Count -gt 0) { exit 2 }
exit 0
