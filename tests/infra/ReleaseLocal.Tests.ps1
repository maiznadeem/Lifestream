# tools/release-local.ps1 makes the release on the owner's PC instead of in release.yml's `release` job. These tests keep the two in
# step (same publish switches, same pack arguments, same asset names, scans before anything leaves the PC) and run the refusals that
# need no build and no network. The whole script is proven by its own -DryRun, which publishes, scans, packs and stops before GitHub.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Path = Join-Path $script:Root 'tools' 'release-local.ps1'
    $script:Text = Get-Content $script:Path -Raw
    $script:Workflow = Get-Content (Join-Path $script:Root '.github' 'workflows' 'release.yml') -Raw
    $hostPath = (Get-Process -Id $PID).Path
    # a pwsh installed as a dotnet tool runs as `dotnet pwsh.dll`: the child needs the pwsh launcher itself
    $script:PwshExe = if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { (Get-Command pwsh -ErrorAction Stop).Source } else { $hostPath }
    function script:Invoke-ReleaseLocal([string[]]$Arguments) {
        $out = & $script:PwshExe -NoProfile -File $script:Path @Arguments 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }
    function script:At([string]$needle) {
        $i = $script:Text.IndexOf($needle, [StringComparison]::Ordinal)
        $i | Should -BeGreaterThan -1 -Because "release-local.ps1 should contain: $needle"
        $i
    }
}

Describe 'tools/release-local.ps1 does what the release job of release.yml does' {
    It 'parses' {
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($script:Path, [ref]$null, [ref]$errors)
        @($errors) | Should -BeNullOrEmpty
    }

    It 'publishes with the switches of release.yml (self-contained, win-x64, ReadyToRun, the version)' {
        $switches = 'publish src/Lifestream.Desktop -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:Version='
        # release.yml folds the command over two lines
        ($script:Workflow -replace '\s+', ' ') | Should -Match ([regex]::Escape("dotnet $switches" + '${{ steps.version.outputs.version }}'))
        $script:Text | Should -Match ([regex]::Escape($switches + '$Version'))
    }

    It 'packs with the argument list of release.yml and the Velopack version it pins' {
        foreach ($pair in "'--packId', 'Lifestream'", "'--mainExe', 'Lifestream.exe'", "'--runtime', 'win-x64'", "'--icon', 'src/Lifestream.Desktop/Assets/Lifestream.ico'") {
            $script:Workflow | Should -Match ([regex]::Escape($pair))
            $script:Text | Should -Match ([regex]::Escape($pair))
        }
        $script:Text | Should -Match ([regex]::Escape("'--packVersion', `$Version"))
        $script:Workflow -match 'VPK_VERSION: (?<v>\S+)' | Should -BeTrue
        $script:Text | Should -Match ([regex]::Escape("`$VpkVersion = '$($Matches.v)'"))
    }

    It 'installs vpk into a tool path, never system-wide' {
        $script:Text | Should -Match 'tool install vpk --version \$VpkVersion --tool-path \$toolsDir'
        $script:Text | Should -Not -Match 'tool install[^\r\n]*(--global|\s-g\b)'
    }

    It 'publishes the six assets under the names the installer page and the updater read' {
        $script:Text | Should -Match ([regex]::Escape('$assetNames = @("Lifestream-$Version-full.nupkg", ''Lifestream-win-Portable.zip'', ''Lifestream-win-Setup.exe'', ''RELEASES'', ''releases.win.json'', ''SHA256SUMS.txt'')'))
    }

    It 'runs in the order: checks, notes, publish, scan, pack, scan, checksums, and only then anything on GitHub' {
        $order = @(
            (At "Step 'Checks (clean tree, origin/master, version, changelog, tag)'"),
            (At "Step 'Release notes from CHANGELOG.md'"),
            (At "Step 'Publish the desktop app (self-contained, win-x64, ReadyToRun)'"),
            (At "Step 'Scan the publish folder and the publish log for credentials'"),
            (At "Step 'Pack the installer and update packages'"),
            (At "Step 'Scan the installer, the update packages and the pack log for credentials'"),
            (At "Step 'Checksums (SHA256SUMS.txt)'"),
            (At 'gh @createArgs'),
            (At 'gh release upload $tag @assets'),
            (At "Step 'Verify the uploaded assets (names, sizes, installer SHA-256)'"),
            (At 'gh @publishArgs'),
            (At "Step 'Verify the published release (tag, commit, assets)'")
        )
        for ($i = 1; $i -lt $order.Count; $i++) { $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i comes after step $($i - 1)" }
    }

    It 'creates the release as a draft at the commit, with the title and the notes file, and publishes it only after the upload is verified' {
        $script:Text | Should -Match ([regex]::Escape('$createArgs = @(''release'', ''create'', $tag, ''--repo'', $Repo, ''--target'', $sha, ''--title'', "Lifestream $Version", ''--notes-file'', $notesFile, ''--draft'')'))
        $script:Text | Should -Match ([regex]::Escape('$publishArgs = @(''release'', ''edit'', $tag, ''--repo'', $Repo, ''--draft=false'')'))
        $script:Text | Should -Match ([regex]::Escape("`$Repo = 'maiznadeem/Lifestream'"))
    }

    It 'a dry run reaches no call that changes GitHub: every one is in the branch a dry run does not take' {
        $else = $script:Text.IndexOf("`n    else {", (At 'if ($DryRun) {'), [StringComparison]::Ordinal)
        $else | Should -BeGreaterThan -1
        foreach ($call in 'gh @createArgs', 'gh release upload $tag @assets', 'gh @publishArgs', 'gh release download') {
            $first = $script:Text.IndexOf($call, [StringComparison]::Ordinal)
            $first | Should -BeGreaterThan $else -Because "$call must not run in a dry run"
        }
        # no git command of the script creates or pushes anything
        $script:Text | Should -Not -Match '(?m)\bgit (push|tag -a|tag v|commit|stash)\b'
    }

    It 'scans with both scanners and through the scanning wrapper (never -SkipGitleaks)' {
        $script:Text | Should -Match "Join-Path \`$toolScripts 'scan-artifacts\.ps1'"
        $script:Text | Should -Match "Join-Path \`$toolScripts 'run-logged\.ps1'"
        $script:Text | Should -Not -Match 'SkipGitleaks'
    }

    It 'takes the release text the way release.yml does (the CHANGELOG section, then the SmartScreen sentence)' {
        foreach ($line in '$lines[$_].StartsWith("## [', "`$lines[`$_] -like '## *'", 'The installer is not code-signed: Windows SmartScreen asks for confirmation (More info, then Run anyway). Compare its SHA-256 with SHA256SUMS.txt first; see the README, Install.') {
            $script:Workflow | Should -Match ([regex]::Escape($line))
            $script:Text | Should -Match ([regex]::Escape($line))
        }
    }

    It 'limits itself to 8 cores at BelowNormal priority by default' {
        $script:Text | Should -Match '\[int\]\$MaxCores = 8'
        $script:Text | Should -Match 'ProcessorAffinity = \[IntPtr\]\(\(\[long\]1 -shl \$cores\) - 1\)'
        $script:Text | Should -Match 'PriorityClass = \[Diagnostics\.ProcessPriorityClass\]::BelowNormal'
    }
}

Describe 'tools/release-local.ps1 refuses (run for real: these stop before any build or network call)' {
    It 'refuses a test override without -DryRun: <_>' -ForEach @('-DryRunAllowExistingTag', '-DryRunAllowOtherCommit') {
        $r = Invoke-ReleaseLocal @('-Version', '9.9.9', $_)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'only work with -DryRun'
    }

    It 'refuses a version that is not <major>.<minor>.<patch>[-suffix]: <_>' -ForEach @('v2.1.1', '2.1', 'latest') {
        $r = Invoke-ReleaseLocal @('-Version', $_, '-DryRun')
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match "(?s)Cannot validate argument on parameter 'Version'"
    }

    It 'refuses a work folder inside the repository (the release needs a clean tree)' {
        $r = Invoke-ReleaseLocal @('-Version', '9.9.9', '-DryRun', '-WorkDir', (Join-Path $script:Root 'artifacts'))
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'is inside the repository'
        Test-Path (Join-Path $script:Root 'artifacts') | Should -BeFalse
    }

    It 'refuses a -RepoRoot that is not a Lifestream checkout' {
        $r = Invoke-ReleaseLocal @('-Version', '9.9.9', '-DryRun', '-RepoRoot', $TestDrive)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'No Lifestream checkout at'
    }
}
