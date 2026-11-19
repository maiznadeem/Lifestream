# INFRA-01 (source tree): the repository scan of secret-scan.yml must be green on ordinary code and red on a real credential.
# Credentials are assembled from fragments so this file is itself clean when the repository is scanned.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Scanner = Join-Path $script:Root 'tools' 'scan-artifacts.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }
    $script:Pw = 'Pass' + 'word'
    $script:Val = 'Zq7x' + 'K2mP9'

    function script:Invoke-Scan([string[]]$ScanArgs, [string]$WorkDir = $script:Root) {
        Push-Location $WorkDir
        try { $out = & $script:Pwsh @script:PwshArgs $script:Scanner @ScanArgs -SkipGitleaks 2>&1 | Out-String; $code = $LASTEXITCODE } finally { Pop-Location }
        [pscustomobject]@{ ExitCode = $code; Output = $out }
    }

    # A throw-away repository root with the given files; returns the root.
    function script:New-Repo([hashtable]$Files) {
        $root = Join-Path $TestDrive ('repo-' + [guid]::NewGuid().ToString('N'))
        foreach ($k in $Files.Keys) {
            $p = Join-Path $root $k; New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
            [IO.File]::WriteAllText($p, $Files[$k], [Text.UTF8Encoding]::new($false))
        }
        $root
    }

    # A throw-away git repository: -Tracked files are git add-ed (forced, so even ignored paths can be tracked), -Untracked are not.
    function script:New-GitRepo([hashtable]$Tracked, [hashtable]$Untracked = @{}) {
        $root = New-Repo $Tracked
        & git -C $root init -q 2>&1 | Out-Null
        foreach ($k in $Tracked.Keys) { & git -C $root add -f -- $k 2>&1 | Out-Null }
        foreach ($k in $Untracked.Keys) {
            $p = Join-Path $root $k; New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
            [IO.File]::WriteAllText($p, $Untracked[$k], [Text.UTF8Encoding]::new($false))
        }
        $root
    }

    function script:Get-LineHash([string]$Line) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Line))).ToLowerInvariant() }
}

Describe 'INFRA-01 source tree: precise scanning of code' {
    It 'the repository scan exactly as secret-scan.yml runs it exits 0 on the current tree' {
        $r = Invoke-Scan @('-Path', 'src,tests,infra,packaging', '-GitTracked')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Output | Should -Match '0 finding\(s\)'
    }

    It 'secret-scan.yml runs that same scan command (local and CI commands are identical)' {
        (Get-Content -Raw (Join-Path $script:Root '.github' 'workflows' 'secret-scan.yml')) | Should -Match 'scan-artifacts\.ps1 -Path src,tests,infra,packaging -GitTracked'
        (Get-Content -Raw (Join-Path $script:Root '.github' 'workflows' 'secret-scan.yml')) | Should -Not -Match 'SkipBuildOutput'
    }

    It 'with -GitTracked, ignored build output (bin, obj, TestResults) is never walked, while tracked and new untracked files are scanned' {
        $bad = 'class B { const string Cs = "Server=a;' + $script:Pw + '=' + $script:Val + '"; }'
        $repo = New-GitRepo -Tracked @{ 'src/App/Real.cs' = 'class R { }'; '.gitignore' = "[Bb]in/`n[Oo]bj/`nTestResults/`n" } `
                            -Untracked @{ 'src/App/bin/Debug/B.cs' = $bad; 'src/App/obj/B.cs' = $bad; 'src/TestResults/B.cs' = $bad; 'src/App/New.cs' = 'class N { }' }
        $r = Invoke-Scan @('-Path', (Join-Path $repo 'src'), '-RepoRoot', $repo, '-GitTracked')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Output | Should -Match 'scanned 2 files'
        # A new file that is not ignored (not yet git add-ed) is scanned too, so a local run sees what the CI checkout will have.
        [IO.File]::WriteAllText((Join-Path $repo 'src/App/Leak.cs'), $bad)
        $r2 = Invoke-Scan @('-Path', (Join-Path $repo 'src'), '-RepoRoot', $repo, '-GitTracked')
        $r2.ExitCode | Should -Be 1 -Because $r2.Output
        $r2.Output | Should -Match 'Leak\.cs'
        $r2.Output | Should -Not -Match 'Debug'
    }

    It 'with -GitTracked, EVERY tracked file is scanned, also one under bin, obj or TestResults, and such a tracked file fails the scan (exit 1) by name: <Rel>' -ForEach @(
        @{ Rel = 'src/App/bin/Release/B.cs' }, @{ Rel = 'src/App/obj/B.cs' }, @{ Rel = 'tests/TestResults/run.log' }
    ) {
        $bad = 'class B { const string Cs = "Server=a;' + $script:Pw + '=' + $script:Val + '"; }'
        $clean = New-GitRepo -Tracked @{ 'src/App/Real.cs' = 'class R { }'; 'tests/T.cs' = 'class T { }'; $Rel = 'nothing secret here' }
        $r = Invoke-Scan @('-Path', 'src,tests', '-RepoRoot', $clean, '-GitTracked') -WorkDir $clean
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match ('tracked build output: ' + [regex]::Escape($Rel))
        $r.Output | Should -Match '0 finding\(s\)'
        $dirty = New-GitRepo -Tracked @{ 'src/App/Real.cs' = 'class R { }'; 'tests/T.cs' = 'class T { }'; $Rel = $bad }
        $d = Invoke-Scan @('-Path', 'src,tests', '-RepoRoot', $dirty, '-GitTracked') -WorkDir $dirty
        $d.ExitCode | Should -Be 1 -Because $d.Output
        $d.Output | Should -Match ('FINDING .*' + [regex]::Escape(($Rel -split '/')[-1]))
    }

    It 'without -GitTracked, a folder is walked completely, including one that is itself named bin (a publish folder)' {
        $bad = 'class B { const string Cs = "Server=a;' + $script:Pw + '=' + $script:Val + '"; }'
        $repo = New-Repo @{ 'bin/B.cs' = $bad; 'src/App/obj/C.cs' = $bad }
        (Invoke-Scan @('-Path', (Join-Path $repo 'bin'), '-RepoRoot', $repo)).ExitCode | Should -Be 1
        (Invoke-Scan @('-Path', (Join-Path $repo 'src'), '-RepoRoot', $repo)).ExitCode | Should -Be 1
    }

    It 'exits 1 on a real connection string planted in a temp .cs file' {
        $repo = New-Repo @{ 'src/App/Db.cs' = ('class Db { const string Cs = "Server=tcp:x.database.windows.net;User ' + 'ID=app;' + $script:Pw + '=' + $script:Val + '"; }') }
        $r = Invoke-Scan @('-Path', (Join-Path $repo 'src'), '-RepoRoot', $repo)
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'Db\.cs'
        $r.Output | Should -Not -Match $script:Val
    }

    It 'exits 1 on a real connection string planted in a new .cs file under the real tree, with the CI command (and the tree is clean without it)' {
        $dir = Join-Path $script:Root 'src' ('zz-scan-plant-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $dir | Out-Null
        try {
            [IO.File]::WriteAllText((Join-Path $dir 'Plant.cs'), ('class P { string S = "Server=a;' + $script:Pw + '=' + $script:Val + '"; }'))
            $r = Invoke-Scan @('-Path', 'src,tests,infra,packaging', '-GitTracked')
            $r.ExitCode | Should -Be 1 -Because $r.Output
            $r.Output | Should -Match 'Plant\.cs'
        } finally { Remove-Item -Recurse -Force $dir }
        (Invoke-Scan @('-Path', 'src,tests,infra,packaging', '-GitTracked')).ExitCode | Should -Be 0
    }

    It 'flags a hard-coded value that is merely written as a property assignment: <Name>' -ForEach @(
        @{ Name = 'cs-literal-assignment';  File = 'src/A.cs';   Text = ('class A { void M() { x.' + 'Pass' + 'word = "hunter22"; } }'); Spaces = $true }
        @{ Name = 'cs-bare-literal';        File = 'src/A.cs';   Text = ('class A { void M() { ' + 'Pass' + 'word = "hunter22"; } }') }
        @{ Name = 'xaml-literal';           File = 'src/V.xaml'; Text = ('<Pass' + 'wordField ' + 'Pass' + 'word="hunter22" />') }
        @{ Name = 'xaml-brace-literal';     File = 'src/V.xaml'; Text = ('<Pass' + 'wordField ' + 'Pass' + 'word="{hunter22" />') }
        @{ Name = 'cs-no-space-assignment'; File = 'src/A.cs';   Text = ('class A { void M() { ' + 'Pass' + 'word=hunter22; } }') }
        @{ Name = 'cs-in-comment';          File = 'src/A.cs';   Text = ('// ' + 'Pass' + 'word = hunter22') }
        @{ Name = 'cs-in-string';           File = 'src/A.cs';   Text = ('var s = "a ' + 'Pass' + 'word = hunter22";') }
        @{ Name = 'tests-not-listed';       File = 'tests/T.cs'; Text = ('var p = new C { ' + 'Pass' + 'word = "hunter22" };') }
        # v-03c: the value on its own line inside a multi-line string or comment. The line looks like an identifier assignment, but it
        # is text: the keyword is inside a literal or a comment, so it is a finding.
        @{ Name = 'cs-verbatim-multiline';  File = 'src/A.cs';   Text = ("class A { const string S = @`"Server=x;`r`n" + 'Pass' + "word = hunter22;`r`n`"; }") }
        @{ Name = 'cs-raw-multiline';       File = 'src/A.cs';   Text = ('class A { const string S = """' + "`r`n    " + 'Pass' + "word = hunter22`r`n    " + '"""; }') }
        @{ Name = 'cs-interp-raw-multiline'; File = 'src/A.cs';  Text = ('class A { string S(int n) => $$"""' + "`n" + 'Pass' + "word = hunter22`n" + '"""; }') }
        @{ Name = 'cs-block-comment';       File = 'src/A.cs';   Text = ("/*`r`n    " + 'Pass' + "word = hunter22`r`n*/`r`nclass A { }") }
        @{ Name = 'cs-doc-comment';         File = 'src/A.cs';   Text = ("/// <summary>`r`n/// " + 'Pass' + "word = hunter22`r`n/// </summary>`r`nclass A { }") }
    ) {
        $repo = New-Repo @{ $File = $Text }
        $r = Invoke-Scan @('-Path', $repo, '-RepoRoot', $repo)
        $r.ExitCode | Should -Be 1 -Because $r.Output
    }

    It 'does not flag XAML bindings and markup extensions: <Text>' -ForEach @(
        @{ Text = '<controls:Pass' + 'wordField Pass' + 'word="{Binding Pass' + 'word, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />' }
        @{ Text = '<Pass' + 'wordBox Pass' + 'word="{StaticResource Placeholder}" />' }
        @{ Text = '<Pass' + 'wordBox Pass' + 'word="{x:Static local:Defaults.Empty}" />' }
        @{ Text = '<Pass' + 'wordBox Pass' + 'word="{TemplateBinding Pass' + 'word}" />' }
    ) {
        $repo = New-Repo @{ 'src/V.xaml' = $Text }
        $r = Invoke-Scan @('-Path', $repo, '-RepoRoot', $repo)
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    It 'does not flag C# property assignments from identifiers and members: <Text>' -ForEach @(
        @{ Text = '                Pass' + 'word = string.Empty;' }
        @{ Text = '            Pass' + 'word = _box.Pass' + 'word;' }
        @{ Text = '            Pass' + 'word = _plain.Text;' }
        @{ Text = '                box.Pass' + 'word = text;' }
        @{ Text = '            _box.Pass' + 'word = Pass' + 'word ?? string.Empty;' }
    ) {
        $repo = New-Repo @{ 'src/A.cs' = $Text }
        $r = Invoke-Scan @('-Path', $repo, '-RepoRoot', $repo)
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    Context 'the reviewed allow-list (tools/secret-scan-allowlist.txt: path | rule | sha256 of the matched line)' {
        BeforeAll {
            $script:Line = '    public const string Pass' + 'word = "not-a-real-' + 'value-for-tests";'
            $script:Repo = New-Repo @{ 'tests/App.Tests/TestSecrets.cs' = ("class F {`r`n" + $script:Line + "`r`n}`r`n") }
            function script:Write-Allow([string]$text) { $f = Join-Path $TestDrive ('allow-' + [guid]::NewGuid().ToString('N') + '.txt'); Set-Content -LiteralPath $f $text; $f }
        }

        It 'flags a test-only literal that is not listed, printing the line number and the line hash to put in the list' {
            $r = Invoke-Scan @('-Path', $script:Repo, '-RepoRoot', $script:Repo, '-Allowlist', (Write-Allow '# empty'), '-ShowMatchHashes')
            $r.ExitCode | Should -Be 1 -Because $r.Output
            $r.Output | Should -Match 'line=2'
            $r.Output | Should -Match (Get-LineHash $script:Line)
        }

        It 'accepts it once the path, rule and hash of the line are listed' {
            $allow = Write-Allow ("tests/App.Tests/TestSecrets.cs|PasswordValue|" + (Get-LineHash $script:Line))
            $r = Invoke-Scan @('-Path', $script:Repo, '-RepoRoot', $script:Repo, '-Allowlist', $allow)
            $r.ExitCode | Should -Be 0 -Because $r.Output
            $r.Output | Should -Match '1 allow-listed'
        }

        It 'flags the line again when it is edited (the hash no longer matches)' {
            $allow = Write-Allow ("tests/App.Tests/TestSecrets.cs|PasswordValue|" + (Get-LineHash $script:Line))
            $edited = New-Repo @{ 'tests/App.Tests/TestSecrets.cs' = ("class F {`r`n" + $script:Line.Replace('not-a-real', 'a-real') + "`r`n}`r`n") }
            $r = Invoke-Scan @('-Path', $edited, '-RepoRoot', $edited, '-Allowlist', $allow)
            $r.ExitCode | Should -Be 1 -Because $r.Output
        }

        It 'does not apply an entry to another file or another rule' {
            $other = New-Repo @{ 'tests/App.Tests/Other.cs' = ("class F {`r`n" + $script:Line + "`r`n}`r`n") }
            $allow = Write-Allow ("tests/App.Tests/TestSecrets.cs|PasswordValue|" + (Get-LineHash $script:Line))
            (Invoke-Scan @('-Path', $other, '-RepoRoot', $other, '-Allowlist', $allow)).ExitCode | Should -Be 1
            $allow2 = Write-Allow ("tests/App.Tests/TestSecrets.cs|UserIdValue|" + (Get-LineHash $script:Line))
            (Invoke-Scan @('-Path', $script:Repo, '-RepoRoot', $script:Repo, '-Allowlist', $allow2)).ExitCode | Should -Be 1
        }

        It 'refuses an entry for any file but a test project''s TestSecrets.cs (product code is never test-only): exit 2 (<Path>)' -ForEach @(
            @{ Path = 'src/Fixture.cs' }, @{ Path = 'tests/App.Tests/Other.cs' }, @{ Path = 'tests/App.Tests/Sub/TestSecrets.cs' }
            @{ Path = 'tests/../src/TestSecrets.cs' }, @{ Path = 'tests/TestSecrets.cs' }, @{ Path = 'src/App/TestSecrets.cs' }
        ) {
            $allow = Write-Allow ("$Path|PasswordValue|" + (Get-LineHash $script:Line))
            $r = Invoke-Scan @('-Path', $script:Repo, '-RepoRoot', $script:Repo, '-Allowlist', $allow)
            $r.ExitCode | Should -Be 2 -Because $r.Output
        }

        It 'refuses a malformed entry: exit 2' {
            $r = Invoke-Scan @('-Path', $script:Repo, '-RepoRoot', $script:Repo, '-Allowlist', (Write-Allow 'tests/App.Tests/TestSecrets.cs|PasswordValue|nothex'))
            $r.ExitCode | Should -Be 2 -Because $r.Output
        }

        It 'the checked-in list has only TestSecrets.cs entries and every one still matches a line of the current tree (no stale entries)' {
            $entries = @(Get-Content (Join-Path $script:Root 'tools' 'secret-scan-allowlist.txt') | Where-Object { $_ -and -not $_.StartsWith('#') })
            foreach ($e in $entries) {
                $path, $rule, $hash = $e -split '\|'
                $path | Should -Match '^tests/[^/]+/TestSecrets\.cs$'
                $lines = Get-Content (Join-Path $script:Root $path)
                @($lines | Where-Object { (Get-LineHash $_) -eq $hash }).Count | Should -BeGreaterThan 0 -Because "$e matches no line"
            }
        }
    }
}
