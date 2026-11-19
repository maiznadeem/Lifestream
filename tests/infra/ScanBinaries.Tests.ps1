# INFRA-01 (binaries): the scanner must find credential literals in REAL compiled .NET assemblies.
# Two earlier versions passed hand-made byte-string tests and failed on real DLLs: in a .NET #US string heap each literal is
# preceded by a length byte, so the UTF-16 unit just before the literal is (previous terminal byte, length byte) -- often a
# CJK/Hangul/Balinese letter -- and a Unicode word-boundary rule rejected a literal that starts with the keyword.
# So these tests BUILD tiny net11.0 class libraries (dotnet build, the SDK from global.json) and scan the produced DLLs.
# Every credential is assembled from fragments so this file is itself clean when the repository is scanned.

BeforeDiscovery {
    $script:Cases = . (Join-Path $PSScriptRoot 'ScanBinaries.Cases.ps1')
}

BeforeAll {
    $script:Cases = . (Join-Path $PSScriptRoot 'ScanBinaries.Cases.ps1')
    $script:Pw = 'Pass' + 'word'; $script:Val = 'Zq7x' + 'K2mP9'
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Scanner = Join-Path $script:Root 'tools' 'scan-artifacts.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    function script:Invoke-Scan([string]$Path) {
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path $Path 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }

    # Where the .NET SDK lives: LIFESTREAM_DOTNET_ROOT, the run's user-local SDK next to the worktrees, or `dotnet` on PATH.
    # DOTNET_ROOT is set for the build child process only, never for this session.
    $candidates = @($env:LIFESTREAM_DOTNET_ROOT, (Join-Path $script:Root '..' '..' 'tools' 'dotnet'), (Split-Path (Get-Command dotnet -ErrorAction SilentlyContinue).Source -ErrorAction SilentlyContinue)) |
        Where-Object { $_ -and (Test-Path (Join-Path $_ ($IsWindows ? 'dotnet.exe' : 'dotnet'))) }
    $script:DotnetDir = if ($candidates) { (Resolve-Path @($candidates)[0]).Path } else { $null }

    function script:Build-Library([string]$Dir, [string]$Source) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Dir 'Lib.csproj') -Encoding utf8 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable></PropertyGroup></Project>'
        Set-Content -LiteralPath (Join-Path $Dir 'Lib.cs') -Encoding utf8 $Source
        $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $script:DotnetDir ($IsWindows ? 'dotnet.exe' : 'dotnet')))
        foreach ($a in 'build', (Join-Path $Dir 'Lib.csproj'), '-c', 'Release', '-o', (Join-Path $Dir 'out'), '--nologo', '-v', 'q') { $psi.ArgumentList.Add($a) }
        $psi.Environment['DOTNET_ROOT'] = $script:DotnetDir
        $psi.Environment['PATH'] = $script:DotnetDir + [IO.Path]::PathSeparator + $env:PATH
        $psi.Environment['DOTNET_NOLOGO'] = '1'; $psi.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $p = [Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) { throw "dotnet build failed for $Dir`n$($stdout.Result)`n$($stderr.Result)" }
        Join-Path $Dir 'out' 'Lib.dll'
    }

    $script:Dlls = @{}
    if ($script:DotnetDir) {
        foreach ($c in $script:Cases) {
            $src = 'namespace P; public static class A { public static string S() => "' + $c.Literal + '"; }'
            $script:Dlls[$c.Name] = Build-Library (Join-Path $TestDrive "dll-$($c.Name)") $src
        }
        # Sweep: 65 literals that start with the keyword, lengths 5..69, in 5 DLLs of 13 (the scanner caps findings per file
        # at 20). Every length byte, hence every possible preceding UTF-16 unit, occurs. Each literal must be its own finding.
        $pwdKey = 'P' + 'wd='
        foreach ($g in 0..4) {
            $methods = foreach ($n in ($g * 13)..($g * 13 + 12)) { "public static string M$n() => `"$pwdKey" + ($script:Val * 8).Substring(0, 1 + $n) + '";' }
            $script:Dlls["sweep$g"] = Build-Library (Join-Path $TestDrive "dll-sweep$g") ('namespace P; public static class A { ' + ($methods -join ' ') + ' }')
        }
    }
}

Describe 'INFRA-01 binaries: credential literals in real compiled net11.0 DLLs' {
    It 'found the .NET SDK to build with' {
        $script:DotnetDir | Should -Not -BeNullOrEmpty -Because 'set LIFESTREAM_DOTNET_ROOT or put the SDK on PATH; a missing SDK must fail, not skip'
    }

    It 'the built DLL really is a .NET assembly with the literal in the #US heap as UTF-16LE (the fixture is real)' -ForEach $script:Cases {
        $bytes = [IO.File]::ReadAllBytes($script:Dlls[$Name])
        [Text.Encoding]::ASCII.GetString($bytes, 0, 2) | Should -Be 'MZ'
        $needle = [Text.Encoding]::Unicode.GetBytes($Literal)
        $hay = [Convert]::ToHexString($bytes); $pat = [Convert]::ToHexString($needle)
        $hay.Contains($pat) | Should -BeTrue
    }

    It 'finds <Name> in the compiled DLL (UTF-16LE)' -ForEach $script:Cases {
        $r = Invoke-Scan $script:Dlls[$Name]
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'UTF-16LE'
        $r.Output | Should -Not -Match $script:Val
    }

    It 'finds every one of 65 keyword-first literals of different lengths (every length byte before a literal): group <G>' -ForEach (0..4 | ForEach-Object { @{ G = $_ } }) {
        $r = Invoke-Scan $script:Dlls["sweep$G"]
        $r.ExitCode | Should -Be 1 -Because $r.Output
        @($r.Output -split "`n" | Where-Object { $_ -match '^FINDING .*encoding=UTF-16LE' }).Count | Should -Be 13 -Because $r.Output
    }

    It 'does not flag a compiled DLL whose literals only mention the keywords (no value)' {
        $src = ('namespace P; public static class A { public static string S() => "' + $script:Pw + '"; public static string T() => "User ID"; public static string U() => "Enter your ' + $script:Pw + ' = "; }')
        $dll = Build-Library (Join-Path $TestDrive 'dll-clean') $src
        $r = Invoke-Scan $dll
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }
}
