#requires -Version 7
<#
.SYNOPSIS
  Makes a release on this PC: what the `release` job of .github/workflows/release.yml does, without GitHub Actions minutes.

.DESCRIPTION
  Same switches and the same asset names as the workflow: Lifestream-win-Setup.exe, Lifestream-win-Portable.zip,
  Lifestream-<version>-full.nupkg, RELEASES, releases.win.json and SHA256SUMS.txt.

  Order (each step is timed; the table is printed at the end and written to <work>/timings.json):
    1. Checks. Refuses unless the working tree is clean, HEAD is the commit origin/master points at on GitHub, the version in
       Directory.Build.props equals -Version, CHANGELOG.md has a section for it, and neither the tag nor the release exists yet.
    2. Publishes the desktop app: self-contained, win-x64, ReadyToRun, -p:Version=<version>.
    3. Scans the publish folder and the publish log for credentials (tools/scan-artifacts.ps1, both scanners).
    4. Installs the Velopack CLI (vpk) as a dotnet tool into <WorkDir>/tools/vpk: a tool path, never system-wide.
    5. Packs the installer and the update packages (vpk pack, the workflow's own argument list).
    6. Scans the installer, the packages and the pack log.
    7. Writes SHA256SUMS.txt.
    8. Takes the release text from this version's section of CHANGELOG.md.
    9. Creates the release as a draft at this commit (gh release create v<version> --target <sha>), uploads the assets, checks
       the uploaded names, sizes and the installer's SHA-256, and only then publishes it, which creates the tag. Then checks the
       published release again: tag at this commit, not a draft, the six assets.
  -DryRun does 1 to 8 and prints what 9 would run. Nothing is tagged, created or uploaded.

  The build and tests are NOT run here: prove them first (the local gate), then release. Nothing in this script starts a GitHub
  Actions run: the tag is created by the release itself, and no workflow is triggered by a tag.

  CPU: the script limits itself and everything it starts to the first -MaxCores cores at BelowNormal priority.

  Signing (optional, as in the workflow): set WINDOWS_SIGNING_PFX (path of the .pfx) and WINDOWS_SIGNING_PFX_PASSWORD in the
  environment. Without them the release is unsigned and SHA-256 checksums are published instead.

  The Microsoft Store MSIX is not built here. It is never a release asset; build it with packaging/store/build-msix.ps1 from
  <work>/publish when it is needed (packaging/store/README.md).

.PARAMETER Version   The version to release, for example 2.1.1 (the tag is v2.1.1). A suffix (2.2.0-beta.1) makes a pre-release.
.PARAMETER DryRun    Everything except tagging, creating the release and uploading.
.PARAMETER RepoRoot  The checkout to release (default: the repository this script is in). HEAD may be a detached origin/master.
.PARAMETER WorkDir   Where the publish folder, the packages, the logs and the vpk tool go (default: lifestream-release in the temp folder).
                     Never inside the repository: the release needs a clean tree.
.PARAMETER MaxCores  How many cores the script and its children may use (default 8).
.PARAMETER DryRunAllowExistingTag   Test only, refused without -DryRun: goes on when the tag or the release already exists.
.PARAMETER DryRunAllowOtherCommit   Test only, refused without -DryRun: goes on when HEAD is not origin/master.

.EXAMPLE  pwsh tools/release-local.ps1 -Version 2.1.1 -DryRun
.EXAMPLE  pwsh tools/release-local.ps1 -Version 2.1.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [switch]$DryRun,
    [string]$RepoRoot,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'lifestream-release'),
    [string]$Repo = 'maiznadeem/lifestream',
    [string]$VpkVersion = '1.2.161',
    [ValidateRange(1, 64)][int]$MaxCores = 8,
    [switch]$DryRunAllowExistingTag,
    [switch]$DryRunAllowOtherCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The checkout to release: -RepoRoot, or the repository this script is in. Its own tools/ (run-logged.ps1, scan-artifacts.ps1) are used,
# so a copy of this script kept outside a checkout works on any checkout.
if (-not $RepoRoot) { $RepoRoot = Join-Path $PSScriptRoot '..' }
if (-not (Test-Path (Join-Path $RepoRoot 'Directory.Build.props'))) { throw "No Lifestream checkout at '$RepoRoot' (no Directory.Build.props). Pass -RepoRoot <path>." }
$root = (Resolve-Path $RepoRoot).Path
$toolScripts = Join-Path $root 'tools'
foreach ($needed in 'run-logged.ps1', 'scan-artifacts.ps1') { if (-not (Test-Path (Join-Path $toolScripts $needed))) { throw "The checkout has no tools/$needed" } }
$tag = "v$Version"
$prerelease = $Version.Contains('-')
$assetNames = @("Lifestream-$Version-full.nupkg", 'Lifestream-win-Portable.zip', 'Lifestream-win-Setup.exe', 'RELEASES', 'releases.win.json', 'SHA256SUMS.txt')
$installer = 'Lifestream-win-Setup.exe'

if (($DryRunAllowExistingTag -or $DryRunAllowOtherCommit) -and -not $DryRun) { throw 'The -DryRunAllow* switches are for tests of this script and only work with -DryRun.' }

# ---- CPU limits: inherited by every process started from here
$cores = [Math]::Min($MaxCores, [Environment]::ProcessorCount)
$self = [Diagnostics.Process]::GetCurrentProcess()
$self.ProcessorAffinity = [IntPtr](([long]1 -shl $cores) - 1)
$self.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
$env:DOTNET_PROCESSOR_COUNT = "$cores"
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:GH_PROMPT_DISABLED = '1'

$WorkDir = [IO.Path]::GetFullPath($WorkDir)
if ($WorkDir.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "-WorkDir ($WorkDir) is inside the repository; the release needs a clean tree. Choose a folder outside it." }
$work = Join-Path $WorkDir $Version
$publish = Join-Path $work 'publish'
$releases = Join-Path $work 'Releases'
$toolsDir = Join-Path $WorkDir 'tools\vpk'
$vpk = Join-Path $toolsDir 'vpk.exe'
$notesFile = Join-Path $work 'release-notes.md'

$timings = [Collections.Generic.List[object]]::new()
function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $Body
    $sw.Stop()
    $timings.Add([pscustomobject]@{ step = $Name; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1) })
    Write-Host ('   done in {0:n1} s' -f $sw.Elapsed.TotalSeconds)
}
# A native command whose failure stops the release. Its output is returned; the caller decides what to print.
function Native([string]$What, [scriptblock]$Call) {
    $out = & $Call 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE): $(($out | Out-String).Trim())" }
    $out
}
# Build output goes to a file, is scanned, and is shown only when the command failed (the wrapper prints a redacted summary on a finding).
function Logged([string]$What, [string]$Log, [string]$Command) {
    $out = & (Join-Path $toolScripts 'run-logged.ps1') -Log $Log -Command $Command 6>&1
    if ($LASTEXITCODE -ne 0) {
        $out | Select-Object -Last 60 | ForEach-Object { Write-Host "   $_" }
        throw "$What failed (exit $LASTEXITCODE). Log: $Log"
    }
}
function Scan([string]$What, [string[]]$Path, [string]$Report) {
    $out = & (Join-Path $toolScripts 'scan-artifacts.ps1') -Path $Path -ReportPath $Report 6>&1 | Out-String
    $code = $LASTEXITCODE
    ($out -split "`r?`n" | Where-Object { $_ -match '^(FINDING|ERROR|scanned)' }) | ForEach-Object { Write-Host "   $_" }
    if ($code -ne 0) { throw "$What: the credential scan exited $code (1 = a credential found, 2 = the scan could not run). Report: $Report" }
}

Push-Location $root
try {
    $sha = $null
    Step 'Checks (clean tree, origin/master, version, changelog, tag)' {
        foreach ($tool in 'git', 'gh', 'dotnet') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on PATH" } }
        $dirty = @(Native 'git status' { git status --porcelain })
        if ($dirty.Count -gt 0) { throw "The working tree is not clean ($($dirty.Count) changed or untracked files). Commit or remove them first." }
        $script:sha = (Native 'git rev-parse' { git rev-parse HEAD } | Select-Object -First 1).Trim()

        # What GitHub has now, asked directly: a stale local origin/master must not pass.
        $remote = @(Native 'git ls-remote' { git ls-remote origin refs/heads/master "refs/tags/$tag" })
        $master = ($remote | Where-Object { $_ -match '\srefs/heads/master$' } | Select-Object -First 1) -replace '\s.*$', ''
        if (-not $master) { throw 'origin has no master branch (git ls-remote returned none).' }
        if ($master -ne $script:sha) {
            $message = "HEAD ($($script:sha.Substring(0, 7))) is not origin/master ($($master.Substring(0, 7))). A release is made from the commit that is on master."
            if ($DryRunAllowOtherCommit) { Write-Host "   TEST OVERRIDE: $message" } else { throw $message }
        }

        $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
        if ($props -notmatch '<Version>(?<v>[^<]+)</Version>') { throw 'Directory.Build.props has no <Version>.' }
        if ($Matches.v -ne $Version) { throw "Directory.Build.props says $($Matches.v), not $Version. Bump the version (and CHANGELOG.md) first." }

        $tagOnRemote = [bool]($remote | Where-Object { $_ -match "\srefs/tags/$([regex]::Escape($tag))$" })
        $tagLocal = [bool](git tag --list $tag)
        gh release view $tag --repo $Repo --json tagName *> $null
        $releaseExists = $LASTEXITCODE -eq 0
        if ($tagOnRemote -or $tagLocal -or $releaseExists) {
            $message = "$tag already exists (tag on GitHub: $tagOnRemote, local tag: $tagLocal, release: $releaseExists). A published version is never replaced."
            if ($DryRunAllowExistingTag) { Write-Host "   TEST OVERRIDE: $message" } else { throw $message }
        }

        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        New-Item -ItemType Directory -Force $work, $toolsDir | Out-Null
        Write-Host "   commit $($script:sha), version $Version, tag $tag, prerelease $($prerelease.ToString().ToLower()), work folder $work"
    }

    # Before anything is built: a release without its text is refused in seconds, not after the pack.
    Step 'Release notes from CHANGELOG.md' {
        $lines = Get-Content (Join-Path $root 'CHANGELOG.md')
        $start = 0..($lines.Count - 1) | Where-Object { $lines[$_].StartsWith("## [$Version]") } | Select-Object -First 1
        if ($null -eq $start) { throw "CHANGELOG.md has no section for $Version" }
        $next = ($start + 1)..($lines.Count - 1) | Where-Object { $lines[$_] -like '## *' } | Select-Object -First 1
        $end = if ($null -ne $next) { $next - 1 } else { $lines.Count - 1 }
        $notes = ($lines[($start + 1)..$end] -join "`n").Trim()
        if (-not $notes) { throw "The CHANGELOG.md section for $Version is empty" }
        $notes += "`n`nThe installer is not code-signed: Windows SmartScreen asks for confirmation (More info, then Run anyway). Compare its SHA-256 with SHA256SUMS.txt first; see the README, Install."
        Set-Content -Path $notesFile -Value $notes -Encoding utf8
        Write-Host "   $(@($notes -split "`n").Count) lines"
    }

    Step 'Publish the desktop app (self-contained, win-x64, ReadyToRun)' {
        Logged 'dotnet publish' (Join-Path $work 'publish.log') "dotnet publish src/Lifestream.Desktop -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:Version=$Version -o `"$publish`""
        $exe = Join-Path $publish 'Lifestream.exe'
        if (-not (Test-Path $exe)) { throw "The publish folder has no Lifestream.exe: $publish" }
        $info = (Get-Item $exe).VersionInfo
        $numeric = ($Version -replace '-.*$', '')
        if (-not $info.FileVersion.StartsWith($numeric)) { throw "Lifestream.exe has file version $($info.FileVersion), expected $numeric" }
        Write-Host "   $(@(Get-ChildItem $publish -Recurse -File).Count) files; Lifestream.exe $($info.FileVersion), product version $($info.ProductVersion)"
    }

    Step 'Scan the publish folder and the publish log for credentials' {
        Scan 'Publish folder' @($publish, (Join-Path $work 'publish.log')) (Join-Path $work 'scan-publish.json')
    }

    Step "Velopack CLI $VpkVersion (dotnet tool in $toolsDir)" {
        $installed = if (Test-Path $vpk) { (dotnet tool list --tool-path $toolsDir 2>$null | Select-String '^vpk\s+(\S+)').Matches | ForEach-Object { $_.Groups[1].Value } } else { $null }
        if ($installed -eq $VpkVersion) { Write-Host "   already installed" }
        else {
            if ($installed) { Native 'dotnet tool uninstall vpk' { dotnet tool uninstall vpk --tool-path $toolsDir } | Out-Null }
            Native 'dotnet tool install vpk' { dotnet tool install vpk --version $VpkVersion --tool-path $toolsDir } | Out-Null
            Write-Host "   installed"
        }
    }

    $signing = [bool]$env:WINDOWS_SIGNING_PFX
    Step 'Pack the installer and update packages' {
        $vpkArgs = @('pack', '--packId', 'Lifestream', '--packVersion', $Version,
            '--packDir', $publish, '--mainExe', 'Lifestream.exe', '--runtime', 'win-x64',
            '--outputDir', $releases,
            '--icon', 'src/Lifestream.Desktop/Assets/Lifestream.ico')
        if ($signing) {
            if (-not (Test-Path $env:WINDOWS_SIGNING_PFX)) { throw 'WINDOWS_SIGNING_PFX is set but the file does not exist.' }
            $vpkArgs += @('--signParams', "/f `"$env:WINDOWS_SIGNING_PFX`" /p `"$env:WINDOWS_SIGNING_PFX_PASSWORD`" /fd sha256 /tr http://timestamp.digicert.com /td sha256")
        } else { Write-Host '   No signing certificate configured: the release is unsigned and Windows SmartScreen will warn. SHA-256 checksums are published instead.' }
        # The wrapper builds the command in its own scope: the tool path and the argument array are passed as global variables.
        $global:releaseLocalVpk = $vpk
        $global:releaseLocalVpkArgs = $vpkArgs
        try { Logged 'vpk pack' (Join-Path $work 'vpk.log') '& $global:releaseLocalVpk @global:releaseLocalVpkArgs' }
        finally { Remove-Variable -Scope Global -Name releaseLocalVpk, releaseLocalVpkArgs -ErrorAction SilentlyContinue }
        if ($signing) {
            $bad = Get-ChildItem $releases -Filter '*Setup.exe' | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }
            if ($bad) { throw "Not validly signed: $($bad.Name -join ', ')" }
        }
    }

    Step 'Scan the installer, the update packages and the pack log for credentials' {
        Scan 'Releases folder' @($releases, (Join-Path $work 'vpk.log')) (Join-Path $work 'scan-releases.json')
    }

    Step 'Checksums (SHA256SUMS.txt)' {
        Get-ChildItem $releases -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
            '{0} *{1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
        } | Set-Content -Path (Join-Path $releases 'SHA256SUMS.txt') -Encoding ascii
        Get-Content (Join-Path $releases 'SHA256SUMS.txt') | ForEach-Object { Write-Host "   $_" }
        $missing = @($assetNames | Where-Object { -not (Test-Path (Join-Path $releases $_)) })
        if ($missing) { throw "The pack did not make: $($missing -join ', ')" }
    }

    $assets = @($assetNames | ForEach-Object { Join-Path $releases $_ })
    $local = @{}
    foreach ($a in $assets) { $local[(Split-Path $a -Leaf)] = (Get-Item $a).Length }
    $installerHash = (Get-FileHash (Join-Path $releases $installer) -Algorithm SHA256).Hash.ToLower()
    $createArgs = @('release', 'create', $tag, '--repo', $Repo, '--target', $sha, '--title', "Lifestream $Version", '--notes-file', $notesFile, '--draft')
    if ($prerelease) { $createArgs += '--prerelease' }
    $publishArgs = @('release', 'edit', $tag, '--repo', $Repo, '--draft=false')
    if (-not $prerelease) { $publishArgs += '--latest' }

    if ($DryRun) {
        Step 'DRY RUN: what the release would run (nothing is tagged, created or uploaded)' {
            Write-Host "   gh $($createArgs -join ' ')"
            foreach ($a in $assets) { Write-Host ("   gh release upload $tag `"$a`" --repo $Repo   ({0:n1} MB)" -f ((Get-Item $a).Length / 1MB)) }
            Write-Host "   gh $($publishArgs -join ' ')"
            Write-Host "   then: check the six asset names, their sizes and the SHA-256 of $installer ($installerHash) on GitHub, and that $tag points at $sha"
        }
    }
    else {
        # A draft has no tag and is not the latest release: the updater and the download page keep serving the previous version until
        # every asset is uploaded and checked. If a step below fails, the draft stays: delete it (gh release delete) and run again.
        Step "Create the draft release $tag at $($sha.Substring(0, 7))" { Native 'gh release create' { gh @createArgs } | Out-Null }
        Step 'Upload the assets' { Native 'gh release upload' { gh release upload $tag @assets --repo $Repo } | Out-Null }
        Step 'Verify the uploaded assets (names, sizes, installer SHA-256)' {
            $draft = (Native 'gh release view' { gh release view $tag --repo $Repo --json assets,isDraft }) | Out-String | ConvertFrom-Json
            $names = @($draft.assets | ForEach-Object name | Sort-Object)
            if (Compare-Object $names ($assetNames | Sort-Object)) { throw "The release has [$($names -join ', ')], expected [$(($assetNames | Sort-Object) -join ', ')]" }
            foreach ($a in $draft.assets) { if ($a.size -ne $local[$a.name]) { throw "$($a.name) is $($a.size) bytes on GitHub and $($local[$a.name]) here" } }
            $remoteAsset = $draft.assets | Where-Object name -eq $installer
            $digest = if ($remoteAsset.PSObject.Properties['digest']) { [string]$remoteAsset.digest } else { '' }
            if ($digest -match '^sha256:(?<h>[0-9a-f]{64})$') { $remoteHash = $Matches.h; $how = 'the digest GitHub computed' }
            else {
                $check = Join-Path $work 'verify'
                New-Item -ItemType Directory -Force $check | Out-Null
                Native 'gh release download' { gh release download $tag --repo $Repo --pattern $installer --dir $check --clobber } | Out-Null
                $remoteHash = (Get-FileHash (Join-Path $check $installer) -Algorithm SHA256).Hash.ToLower(); $how = 'the downloaded file'
            }
            if ($remoteHash -ne $installerHash) { throw "$installer on GitHub has SHA-256 $remoteHash ($how), SHA256SUMS.txt says $installerHash" }
            Write-Host "   6 assets, sizes equal; $installer SHA-256 $remoteHash ($how) = SHA256SUMS.txt"
        }
        Step "Publish the release (creates the tag $tag)" { Native 'gh release edit' { gh @publishArgs } | Out-Null }
        Step 'Verify the published release (tag, commit, assets)' {
            $published = (Native 'gh api release' { gh api "repos/$Repo/releases/tags/$tag" }) | Out-String | ConvertFrom-Json
            if ($published.draft) { throw "$tag is still a draft" }
            $names = @($published.assets | ForEach-Object name | Sort-Object)
            if (Compare-Object $names ($assetNames | Sort-Object)) { throw "The published release has [$($names -join ', ')]" }
            $ref = (Native 'git ls-remote' { git ls-remote origin "refs/tags/$tag" } | Select-Object -First 1) -replace '\s.*$', ''
            if ($ref -ne $sha) { throw "$tag points at $ref on GitHub, expected $sha" }
            Native 'git fetch tag' { git fetch --quiet origin "refs/tags/${tag}:refs/tags/$tag" } | Out-Null
            Write-Host "   $($published.html_url): $($names.Count) assets, tag $tag at $($sha.Substring(0, 7))"
        }
    }

    $total = [Math]::Round((($timings | Measure-Object seconds -Sum).Sum), 1)
    $timings | ConvertTo-Json | Set-Content (Join-Path $work 'timings.json') -Encoding utf8
    Write-Host ''
    Write-Host ('{0,8}  {1}' -f 'seconds', 'step')
    foreach ($t in $timings) { Write-Host ('{0,8:n1}  {1}' -f $t.seconds, $t.step) }
    Write-Host ('{0,8:n1}  {1}' -f $total, 'TOTAL')
    Write-Host $(if ($DryRun) { "DRY RUN finished: nothing was published. Output: $work" } else { "RELEASED $tag. Output: $work" })
}
finally { Pop-Location }
