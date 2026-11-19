# INFRA-01 (source tree): throw-away test passwords live in ONE file per test project, tests/<Project>/TestSecrets.cs, and only
# that file's lines may be on the source allow-list. Every new test that typed a password literal into its own file used to turn
# the repository scan red; this guard fails first, and names the file and line, so the fix is "use TestSecrets", not "allow-list it".
#
# A password-shaped literal is a non-empty C# string literal
#   (a) assigned, initialised or passed by name to anything whose name contains "password", "passwd" or "pwd", in code
#       (a property set, an object initialiser, a local or const, an anonymous-object member, a named argument),
#   (b) assigned to a dictionary/JSON key whose name contains password (body["initialPassword"] gets a literal), or
#   (c) written inside a string as a JSON pair whose key contains password and whose value is a literal, unless the value is an
#       interpolation hole ({x} or {{x}}, e.g. a hole that reads TestSecrets.WrongPrefix).
# The planted cases below spell each shape out (assembled from fragments so this file stays clean when scanned).
# A literal passed by position (Login(name, "x")) has no shape to recognise and is not covered; the repository scan does not flag it
# either. tests/infra is not C# and builds its credentials from fragments.

BeforeDiscovery {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Projects = @(Get-ChildItem (Join-Path $script:Root 'tests') -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") } | ForEach-Object Name)
}

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    if (-not ('LifestreamScan.Engine' -as [type])) { Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $script:Root 'tools' 'scan-engine.cs'))) }
    $ci = [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant
    $name = '\w*(?:pass(?:word|wd)|pwd)\w*'
    $script:InCode = @(
        [regex]::new('\b' + $name + '\s*(?:=(?![=>])|:(?!:))\s*[@$]*"(?!")', $ci),
        [regex]::new('\[\s*"' + $name + '"\s*\]\s*=\s*[@$]*"(?!")', $ci)
    )
    $script:InString = [regex]::new('\\?"' + $name + '\\?"\s*:\s*\\?"(?!\{|\\?")', $ci)

    # Every password-shaped literal in one C# file: "path:line: text".
    function script:Find-PasswordLiterals([string]$Path, [string]$Rel) {
        $text = [IO.File]::ReadAllText($Path)
        $code = [LifestreamScan.Engine]::ClassifyCSharp($text)
        $hits = foreach ($rx in $script:InCode) { foreach ($m in $rx.Matches($text)) { if ($code[$m.Index]) { $m } } }
        $hits = @($hits) + @(foreach ($m in $script:InString.Matches($text)) { if (-not $code[$m.Index]) { $m } })
        foreach ($m in $hits | Sort-Object Index -Unique) {
            $line = 1 + ($text.Substring(0, $m.Index).Split("`n").Count - 1)
            '{0}:{1}: {2}' -f $Rel, $line, ($text.Split("`n")[$line - 1].Trim())
        }
    }

    # The C# files git lists under tests/ (tracked, and new files that are not ignored), as the repository scan sees them.
    $listed = ((& git -C $script:Root ls-files -z --cached --others --exclude-standard -- tests) -join '') -split "`0" | Where-Object { $_ -like '*.cs' }
    $script:CsFiles = @($listed | Where-Object { $_ -notmatch '(^|/)(bin|obj|TestResults)/' -and (Test-Path -LiteralPath (Join-Path $script:Root $_)) })
}

Describe 'INFRA-01 test passwords live only in each test project''s TestSecrets.cs' {
    It 'found the test C# files to check' {
        $script:CsFiles.Count | Should -BeGreaterThan 50
    }

    It 'no test file other than a TestSecrets.cs holds a password-shaped literal' {
        $offenders = foreach ($rel in $script:CsFiles) {
            if ((Split-Path $rel -Leaf) -eq 'TestSecrets.cs') { continue }
            Find-PasswordLiterals (Join-Path $script:Root $rel) $rel
        }
        @($offenders) | Should -BeNullOrEmpty -Because ("move these values into the project's TestSecrets.cs and use the constant:`n" + (@($offenders) -join "`n"))
    }

    It 'the guard recognises each shape it promises to (planted in a temporary file): <Text>' -ForEach @(
        @{ Text = 'class T { void M() { vm.Pass' + 'word = "p"; } }' }
        @{ Text = 'class T { void M() { var pass' + 'word = "Owner-" + System.Guid.NewGuid(); } }' }
        @{ Text = 'class T { const string newPass' + 'word = "a-brand-new-1"; }' }
        @{ Text = 'class T { object O() => new { currentPass' + 'word = "a" }; }' }
        @{ Text = 'class T { void M() { Login(user: "u", pass' + 'word: "x"); } }' }
        @{ Text = 'class T { void M() { body["initialPass' + 'word"] = "a-long-one"; } }' }
        @{ Text = 'class T { string J = """{"user":"u","pass' + 'word":"wrong-1"}"""; }' }
        @{ Text = 'class T { string J = "{\"pass' + 'word\":\"x\"}"; }' }
        @{ Text = 'class T { void M() { form.Pw' + 'd = @"x"; } }' }
    ) {
        $f = Join-Path $TestDrive ('Plant' + [guid]::NewGuid().ToString('N') + '.cs')
        [IO.File]::WriteAllText($f, $Text)
        @(Find-PasswordLiterals $f 'plant.cs').Count | Should -Be 1 -Because $Text
    }

    It 'the guard does not flag code that only names passwords: <Text>' -ForEach @(
        @{ Text = 'class T { void M() { vm.Pass' + 'word = TestSecrets.Short; } }' }
        @{ Text = 'class T { string J = $$"""{"pass' + 'word":"{{TestSecrets.Wrong}}-{{i}}"}"""; }' }
        @{ Text = 'class T { void M() { Assert.DoesNotContain("pass' + 'word:", texts); } }' }
        @{ Text = 'class T { void M() { vm.Pass' + 'word = ""; } }' }
        @{ Text = 'class T { bool B(string pass' + 'word) => pass' + 'word == "x"; }' }
    ) {
        $f = Join-Path $TestDrive ('Clean' + [guid]::NewGuid().ToString('N') + '.cs')
        [IO.File]::WriteAllText($f, $Text)
        @(Find-PasswordLiterals $f 'clean.cs') | Should -BeNullOrEmpty -Because $Text
    }

    It 'every source allow-list entry is a line of a TestSecrets.cs' {
        $entries = Get-Content (Join-Path $script:Root 'tools' 'secret-scan-allowlist.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
        foreach ($e in $entries) { ($e -split '\|')[0] | Should -Match '^tests/[^/]+/TestSecrets\.cs$' -Because $e }
    }

    It '<_> keeps its throw-away passwords in tests/<_>/TestSecrets.cs, if it has any' -ForEach $script:Projects {
        $dir = Join-Path $script:Root 'tests' $_
        $secrets = Join-Path $dir 'TestSecrets.cs'
        $uses = @(Get-ChildItem $dir -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -ne 'TestSecrets.cs' } |
            Select-String -Pattern '\bTestSecrets\.' -List)
        if ($uses.Count -gt 0) { Test-Path $secrets | Should -BeTrue }
        if (Test-Path $secrets) { (Get-Content -Raw $secrets) | Should -Match 'static class TestSecrets' }
    }
}
