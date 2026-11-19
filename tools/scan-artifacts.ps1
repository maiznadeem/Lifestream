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
  anything else exits 2). Test passwords belong in each test project's TestSecrets.cs (tests/infra/TestSecrets.Tests.ps1).
  Third-party files: a benign string that the binary rules match (help text, API names) is accepted only when
  tools/secret-scan-binary-allowlist.txt lists it: "file name|rule|sha256 of the matched text". File names starting with
  Lifestream or appsettings are refused (exit 2): the product's own files are never "third-party".

  A finding names the file, rule, encoding, byte offset (and line, in source), never the value and, by default, never a hash of
  it: in a public CI log a SHA-256 of a short or guessable value can be brute-forced offline. To get the matchSha256 / lineSha256
  an allow-list entry needs, run locally with -ShowMatchHashes; it is refused (exit 2) when CI or GITHUB_ACTIONS is set.

  Exit codes: 0 clean; 1 at least one finding, or (with -GitTracked) a tracked file under a bin, obj or TestResults folder;
  2 nothing was scanned (path missing, no files, a read error, a bad allow-list), because a scan that looked at nothing proves nothing.

.PARAMETER Path              One or more files or folders.
.PARAMETER ReportPath        Optional JSON report: files scanned, bytes, findings (no values), errors, timing.
.PARAMETER Allowlist         Reviewed source allow-list (default: tools/secret-scan-allowlist.txt).
.PARAMETER BinaryAllowlist   Reviewed third-party allow-list (default: tools/secret-scan-binary-allowlist.txt).
.PARAMETER RepoRoot          What source allow-list paths and -GitTracked are relative to (default: the folder above tools/).
.PARAMETER MaxArchiveDepth   How deep to open archives inside archives (default 6).
.PARAMETER MaxFindingsPerFile  How many findings to list per file (default 20; every further one still fails the scan).
.PARAMETER ShowMatchHashes   Local review only: also print and report the SHA-256 of each matched text (and, in source, of its
                             line) for an allow-list entry. Refused when $env:CI or $env:GITHUB_ACTIONS is set.
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
    [switch]$GitTracked,
    [switch]$ShowMatchHashes,
    [switch]$SkipGitleaks,
    [string]$GitleaksAllowlist,
    [string]$GitleaksConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$started = [Diagnostics.Stopwatch]::StartNew()
if ($ShowMatchHashes -and ($env:CI -or $env:GITHUB_ACTIONS)) {
    Write-Host 'ERROR: -ShowMatchHashes is for a local review only and is refused in CI (CI or GITHUB_ACTIONS is set): a hash of a matched value must never reach a CI log or report.'
    exit 2
}

$sourceExtensions = '.cs', '.csx', '.razor', '.cshtml', '.xaml', '.axaml'
$buildOutputSegments = 'bin', 'obj', 'TestResults'
$ruleNames = 'PasswordValue', 'UserIdValue', 'SecretValue', 'SqlHostCreds', 'UrlCredentials', 'Jwt', 'PrivateKey'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $Allowlist) { $Allowlist = Join-Path $PSScriptRoot 'secret-scan-allowlist.txt' }
if (-not $BinaryAllowlist) { $BinaryAllowlist = Join-Path $PSScriptRoot 'secret-scan-binary-allowlist.txt' }
if (-not $GitleaksAllowlist) { $GitleaksAllowlist = Join-Path $PSScriptRoot 'gitleaks-allowlist.txt' }
if (-not $GitleaksConfig) { $GitleaksConfig = Join-Path $PSScriptRoot 'gitleaks-lifestream.toml' }

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
$engine.ShowHashes = [bool]$ShowMatchHashes

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
        if ($entryPath -notmatch '^tests/[^/.][^/]*/TestSecrets\.cs$') { Write-Host "ERROR: ${Allowlist}:$n lists $entryPath; only a test project's TestSecrets.cs (tests/<Project>/TestSecrets.cs) may be allow-listed"; exit 2 }
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

# Second scanner (gitleaks): file name|rule|sha256 of the matched text, same rules as above (never the product's own files).
$gitleaksAllow = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if (Test-Path -LiteralPath $GitleaksAllowlist) {
    $n = 0
    foreach ($raw in Get-Content -LiteralPath $GitleaksAllowlist) {
        $n++
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -ne 3 -or $parts[2] -notmatch '^[0-9a-f]{64}$' -or -not $parts[1].Trim()) { Write-Host "ERROR: ${GitleaksAllowlist}:$n is not file-name|rule|sha256"; exit 2 }
        $name = $parts[0].Trim()
        if (-not $name -or $name -match '[\\/*?:]' -or $name.Contains('..')) { Write-Host "ERROR: ${GitleaksAllowlist}:$n '$name' must be a plain file name (no path, no wildcard)"; exit 2 }
        if ($name -match '^(?i)(lifestream|appsettings)') { Write-Host "ERROR: ${GitleaksAllowlist}:$n lists $name; the product's own files (Lifestream*, appsettings*) may not be allow-listed"; exit 2 }
        [void]$gitleaksAllow.Add(('{0}|{1}|{2}' -f $name.ToLowerInvariant(), $parts[1].Trim(), $parts[2]))
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

# The second scanner runs on artifacts, not on the source tree (secret-scan.yml's gitleaks job covers the repository and its history).
$useGitleaks = -not $GitTracked -and -not $SkipGitleaks
$dumpDir = $null
if ($useGitleaks) {
    $dumpDir = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-scan-dump-' + [guid]::NewGuid().ToString('N'))
    $engine.DumpDir = $dumpDir
}
$gitleaksFindings = [System.Collections.Generic.List[object]]::new()
$gitleaksErrors = [System.Collections.Generic.List[string]]::new()
$gitleaksAllowListed = 0
$gitleaksUsed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$gitleaksRuns = 0
try {
    foreach ($file in $files) { $engine.ScanFile($file, (Get-SourceRel $file)) }
    if ($useGitleaks) {
        $glExe = @(& (Join-Path $PSScriptRoot 'fetch-gitleaks.ps1')) | Select-Object -Last 1
        if (-not $glExe -or -not (Test-Path -LiteralPath ([string]$glExe))) { Write-Host 'ERROR: the second scanner (gitleaks) is not available: tools/fetch-gitleaks.ps1 failed'; exit 2 }
        $glExe = [string]$glExe
        # (a) the artifact folders and files as they are (gitleaks opens archives itself), (b) the text dump of every string the
        # extractor found: metadata, resources, BAML, decompressed resources, archive text members, UTF-16 text decoded.
        $targets = [System.Collections.Generic.List[object]]::new()
        foreach ($p in $expanded) { $targets.Add(@{ Path = (Resolve-Path -LiteralPath $p).Path; Kind = 'artifact' }) }
        if (Test-Path -LiteralPath $dumpDir) { $targets.Add(@{ Path = $dumpDir; Kind = 'dump' }) }
        foreach ($t in $targets) {
            $gitleaksRuns++
            $reportFile = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-gitleaks-' + [guid]::NewGuid().ToString('N') + '.json')
            try {
                # --exit-code 99: 99 = leaks found, 0 = clean, anything else (1 = usage or runtime error) = the scan itself failed.
                $out = & $glExe dir $t.Path -c $GitleaksConfig --no-banner --no-color --log-level error --max-archive-depth $MaxArchiveDepth `
                    -f json -r $reportFile --exit-code 99 2>&1 | Out-String
                $code = $LASTEXITCODE
                if ($code -ne 0 -and $code -ne 99) { $gitleaksErrors.Add("gitleaks failed (exit $code) on $($t.Kind) $($t.Path): $(($out -split "`n" | Select-Object -First 3) -join ' ')"); continue }
                if ($code -eq 99) {
                    $items = @(Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json)
                    foreach ($it in $items) {
                        $label = [string]$it.File
                        if ($t.Kind -eq 'dump') {
                            # The dump file holds "<marker> <label>" lines; the finding belongs to the nearest one above its line.
                            $markerPrefix = $engine.DumpMarker + ' '
                            $cur = $null; $ln = 0
                            foreach ($l in [IO.File]::ReadLines([string]$it.File)) { $ln++; if ($ln -gt $it.StartLine) { break }; if ($l.StartsWith($markerPrefix)) { $cur = $l.Substring($markerPrefix.Length) } }
                            $label = if ($cur) { $cur } else { "(string dump $([IO.Path]::GetFileName($label)))" }
                        }
                        $leaf = ($label -split '[\\/!]' | Where-Object { $_ } | Select-Object -Last 1)
                        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]$it.Match))).ToLowerInvariant()
                        $entry = '{0}|{1}|{2}' -f $leaf.ToLowerInvariant(), $it.RuleID, $sha
                        if ($gitleaksAllow.Contains($entry)) { $gitleaksAllowListed++; [void]$gitleaksUsed.Add($entry); continue }
                        $f = [ordered]@{ scanner = 'gitleaks'; file = $label; rule = [string]$it.RuleID; line = [int]$it.StartLine; source = $t.Kind }
                        if ($ShowMatchHashes) { $f.matchSha256 = $sha }
                        $gitleaksFindings.Add([pscustomobject]$f)
                    }
                }
            } finally { Remove-Item -LiteralPath $reportFile -Force -ErrorAction SilentlyContinue }
        }
    }
} finally {
    if ($dumpDir) { Remove-Item -LiteralPath $dumpDir -Recurse -Force -ErrorAction SilentlyContinue }
}
$started.Stop()

$findings = @($engine.Findings | ForEach-Object {
    $f = [ordered]@{ file = $_.File; rule = $_.Rule; encoding = $_.Encoding; offset = $_.Offset }
    if ($_.Line -gt 0) { $f.line = $_.Line }
    if ($_.MatchSha256) { $f.matchSha256 = $_.MatchSha256; $f.matchText = $_.MatchText }
    if ($_.LineSha256) { $f.lineSha256 = $_.LineSha256 }
    [pscustomobject]$f
})
if ($ReportPath) {
    [pscustomobject]@{
        paths = @($Path); filesScanned = $engine.FilesScanned; bytesScanned = $engine.BytesScanned; seconds = [Math]::Round($started.Elapsed.TotalSeconds, 1)
        findings = $findings; findingsOverCap = $engine.FindingsOverCap; allowListed = $engine.AllowListed; allowListEntriesUsed = @($engine.AllowUsed | Sort-Object)
        errors = @($engine.Errors) + @($gitleaksErrors); metadataErrors = $engine.MetadataErrors; trackedBuildOutput = @($trackedBuildOutput)
        gitleaks = [pscustomobject]@{ ran = $useGitleaks; runs = $gitleaksRuns; findings = @($gitleaksFindings); allowListed = $gitleaksAllowListed; allowListEntriesUsed = @($gitleaksUsed | Sort-Object); dumpedStrings = $engine.DumpedStrings }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}

foreach ($f in $findings) {
    $tail = ''
    if ($f.PSObject.Properties['line']) { $tail += ' line={0}' -f $f.line }
    if ($f.PSObject.Properties['matchSha256']) { $tail += ' matchSha256={0}' -f $f.matchSha256 }
    if ($f.PSObject.Properties['lineSha256']) { $tail += ' lineSha256={0}' -f $f.lineSha256 }
    Write-Host ("FINDING rule={0} encoding={1} offset={2} file={3}{4}" -f $f.rule, $f.encoding, $f.offset, $f.file, $tail)
}
if ($engine.FindingsOverCap -gt 0) { Write-Host "($($engine.FindingsOverCap) more finding(s) not listed: over $MaxFindingsPerFile per file)" }
foreach ($f in $gitleaksFindings) {
    $tail = if ($f.PSObject.Properties['matchSha256']) { ' matchSha256={0}' -f $f.matchSha256 } else { '' }
    Write-Host ("FINDING scanner=gitleaks rule={0} line={1} file={2}{3}" -f $f.rule, $f.line, $f.file, $tail)
}
foreach ($e in $gitleaksErrors) { Write-Host "ERROR: $e" }
foreach ($t in $trackedBuildOutput) { Write-Host "ERROR: tracked build output: $t (bin, obj and TestResults folders must not be in git; run git rm --cached on it)" }
foreach ($e in $engine.Errors) { Write-Host "ERROR: $e" }
$glNote = if ($useGitleaks) { "$($gitleaksFindings.Count) finding(s) in $gitleaksRuns run(s), $($engine.DumpedStrings) strings dumped" } else { 'not run' }
Write-Host ("scanned {0} files, {1:N0} bytes: {2} finding(s), {3} error(s), {4} allow-listed, {5:N1} s; gitleaks: {6}" -f $engine.FilesScanned, $engine.BytesScanned, ($findings.Count + $engine.FindingsOverCap + $gitleaksFindings.Count), ($engine.Errors.Count + $gitleaksErrors.Count), ($engine.AllowListed + $gitleaksAllowListed), $started.Elapsed.TotalSeconds, $glNote)
if ($findings.Count -gt 0 -or $engine.FindingsOverCap -gt 0 -or $gitleaksFindings.Count -gt 0 -or $trackedBuildOutput.Count -gt 0) { exit 1 }
if ($engine.Errors.Count -gt 0 -or $gitleaksErrors.Count -gt 0) { exit 2 }
exit 0
