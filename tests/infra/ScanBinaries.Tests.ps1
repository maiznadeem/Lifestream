# INFRA-01 (binaries): the scanner must find credential literals in REAL compiled .NET assemblies.
# Three earlier versions passed hand-made byte-string tests and failed on real DLLs, each time because a keyword test looked at
# the byte BEFORE the keyword: in a .NET #US heap a literal is preceded by its length byte (seen as part of a UTF-16 unit), and in
# custom-attribute blobs, .resources and BAML a UTF-8 string is preceded by its length byte, which is an ASCII letter, digit or
# punctuation for about half of all lengths. So these tests BUILD tiny net11.0 class libraries (dotnet build, the SDK from
# global.json) and scan the produced DLLs, for every string length from 1 to 200 in each of the three places.
# Every credential is assembled from fragments so this file is itself clean when the repository is scanned.

BeforeDiscovery {
    $script:Cases = . (Join-Path $PSScriptRoot 'ScanBinaries.Cases.ps1')
}

BeforeAll {
    $script:Cases = . (Join-Path $PSScriptRoot 'ScanBinaries.Cases.ps1')
    $script:Pw = 'Pass' + 'word'; $script:PwdKey = 'P' + 'wd='; $script:Val = 'Zq7x' + 'K2mP9'
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Scanner = Join-Path $script:Root 'tools' 'scan-artifacts.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    function script:Invoke-Scan([string]$Path, [string[]]$Extra = @()) {
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path $Path @Extra 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }
    function script:Get-Sha([string]$s) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s))).ToLowerInvariant() }

    # Where the .NET SDK lives: LIFESTREAM_DOTNET_ROOT, the run's user-local SDK next to the worktrees, or `dotnet` on PATH.
    # DOTNET_ROOT is set for the build child process only, never for this session.
    $candidates = @($env:LIFESTREAM_DOTNET_ROOT, (Join-Path $script:Root '..' '..' 'tools' 'dotnet'), (Split-Path (Get-Command dotnet -ErrorAction SilentlyContinue).Source -ErrorAction SilentlyContinue)) |
        Where-Object { $_ -and (Test-Path (Join-Path $_ ($IsWindows ? 'dotnet.exe' : 'dotnet'))) }
    $script:DotnetDir = if ($candidates) { (Resolve-Path @($candidates)[0]).Path } else { $null }

    # Writes a class library: Lib.cs, and Strings.resx when $Resx (name -> value) is given. Returns the build process.
    function script:Start-Library([string]$Dir, [string]$Source, [System.Collections.IDictionary]$Resx) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Dir 'Lib.csproj') -Encoding utf8 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable><GenerateDocumentationFile>false</GenerateDocumentationFile></PropertyGroup></Project>'
        Set-Content -LiteralPath (Join-Path $Dir 'Lib.cs') -Encoding utf8 $Source
        if ($Resx) {
            $data = foreach ($k in $Resx.Keys) { '<data name="{0}" xml:space="preserve"><value>{1}</value></data>' -f $k, [Security.SecurityElement]::Escape($Resx[$k]) }
            Set-Content -LiteralPath (Join-Path $Dir 'Strings.resx') -Encoding utf8 ('<?xml version="1.0" encoding="utf-8"?><root>' +
                '<resheader name="resmimetype"><value>text/microsoft-resx</value></resheader><resheader name="version"><value>2.0</value></resheader>' +
                '<resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>' +
                '<resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>' +
                ($data -join '') + '</root>')
        }
        $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $script:DotnetDir ($IsWindows ? 'dotnet.exe' : 'dotnet')))
        foreach ($a in 'build', (Join-Path $Dir 'Lib.csproj'), '-c', 'Release', '-o', (Join-Path $Dir 'out'), '--nologo', '-v', 'q') { $psi.ArgumentList.Add($a) }
        $psi.Environment['DOTNET_ROOT'] = $script:DotnetDir
        $psi.Environment['PATH'] = $script:DotnetDir + [IO.Path]::PathSeparator + $env:PATH
        $psi.Environment['DOTNET_NOLOGO'] = '1'; $psi.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $p = [Diagnostics.Process]::Start($psi)
        [pscustomobject]@{ Dir = $Dir; Process = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync() }
    }
    function script:Wait-Library($b) {
        $b.Process.WaitForExit()
        if ($b.Process.ExitCode -ne 0) { throw "dotnet build failed for $($b.Dir)`n$($b.Out.Result)`n$($b.Err.Result)" }
        Join-Path $b.Dir 'out' 'Lib.dll'
    }
    function script:Escape-Cs([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }
    # One library for one literal of the given kind.
    function script:Start-Case([string]$Dir, [string]$Kind, [string]$Literal) {
        switch ($Kind) {
            'us'   { Start-Library $Dir ('namespace P; public static class A { public static string S() => "' + (Escape-Cs $Literal) + '"; }') $null }
            'attr' { Start-Library $Dir ('[assembly: System.Reflection.AssemblyMetadata("cs", "' + (Escape-Cs $Literal) + '")]' + "`nnamespace P; public static class A { public static int N() => 1; }") $null }
            'resx' { Start-Library $Dir 'namespace P; public static class A { public static int N() => 1; }' ([ordered]@{ Cs = $Literal }) }
        }
    }

    # Sweep values: "Pwd=" followed by a value of 1..200 characters, all different. The literal is 5..204 bytes long, so the length
    # in front of it takes every 1-byte value from 5 to 127 and the 2-byte form from 128 on.
    $alphabet = 'Zq7xK2mP9aB3cD4eF5gH6jL8nR0sT1vW'
    $script:SweepValues = foreach ($n in 1..200) { (($alphabet * 8).Substring($n % 32, $n)) }
    $script:SweepLiterals = foreach ($v in $script:SweepValues) { $script:PwdKey + $v }

    $script:Dlls = @{}
    if ($script:DotnetDir) {
        $builds = @{}
        foreach ($c in $script:Cases) { $builds[$c.Name] = Start-Case (Join-Path $TestDrive "dll-$($c.Name)") $c.Kind $c.Literal }
        $us = for ($i = 0; $i -lt 200; $i++) { 'public static string M' + $i + '() => "' + $script:SweepLiterals[$i] + '";' }
        $builds['sweep-us'] = Start-Library (Join-Path $TestDrive 'dll-sweep-us') ('namespace P; public static class A { ' + ($us -join ' ') + ' }') $null
        $attrs = for ($i = 0; $i -lt 200; $i++) { '[assembly: System.Reflection.AssemblyMetadata("k' + $i + '", "' + $script:SweepLiterals[$i] + '")]' }
        $builds['sweep-attr'] = Start-Library (Join-Path $TestDrive 'dll-sweep-attr') (($attrs -join "`n") + "`nnamespace P; public static class A { public static int N() => 1; }") $null
        $resx = [ordered]@{}; for ($i = 0; $i -lt 200; $i++) { $resx["S$i"] = $script:SweepLiterals[$i] }
        $builds['sweep-resx'] = Start-Library (Join-Path $TestDrive 'dll-sweep-resx') 'namespace P; public static class A { public static int N() => 1; }' $resx
        # Clean libraries: literals that only mention the keywords, and a record with a Password property (its compiled ToString text
        # "Password = " is followed in the #US heap by the next entry).
        $builds['clean'] = Start-Library (Join-Path $TestDrive 'dll-clean') ('namespace P; public static class A { public static string S() => "' + $script:Pw + '"; public static string T() => "User ID"; public static string U() => "Enter your ' + $script:Pw + ' = "; public static string V() => "' + $script:Pw + ': "; }') $null
        $builds['record'] = Start-Library (Join-Path $TestDrive 'dll-record') ('namespace P; public sealed record LoginRequest(string UserName, string ' + $script:Pw + '); public sealed record Db(string Server, string Database, string ' + $script:Pw + ', int Port);') $null
        foreach ($k in $builds.Keys) { $script:Dlls[$k] = Wait-Library $builds[$k] }
    }
}

Describe 'INFRA-01 binaries: credential literals in real compiled net11.0 DLLs' {
    It 'found the .NET SDK to build with' {
        $script:DotnetDir | Should -Not -BeNullOrEmpty -Because 'set LIFESTREAM_DOTNET_ROOT or put the SDK on PATH; a missing SDK must fail, not skip'
    }

    It 'the built DLL really holds <Name> where the compiler puts a <Kind> string (the fixture is real)' -ForEach $script:Cases {
        $bytes = [IO.File]::ReadAllBytes($script:Dlls[$Name])
        [Text.Encoding]::ASCII.GetString($bytes, 0, 2) | Should -Be 'MZ'
        $utf8 = [Text.Encoding]::UTF8.GetBytes($Literal)
        # us: UTF-16LE in the #US heap. attr and resx: UTF-8 preceded by its length (1 byte below 128).
        $needle = switch ($Kind) { 'us' { [Text.Encoding]::Unicode.GetBytes($Literal) } default { [byte[]]@($utf8.Length) + $utf8 } }
        [Convert]::ToHexString($bytes).Contains([Convert]::ToHexString($needle)) | Should -BeTrue
    }

    It 'finds <Name> (<Kind>) in the compiled DLL and never prints the value' -ForEach $script:Cases {
        $r = Invoke-Scan $script:Dlls[$Name]
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Not -Match $script:Val
        $r.Output | Should -Not -Match 'Summer-2024'
    }

    It 'finds every one of 200 "Pwd=" literals with values of 1..200 characters, compiled as <Kind> strings' -ForEach @(
        @{ Kind = 'user-string (#US, UTF-16)'; Dll = 'sweep-us' }
        @{ Kind = 'custom-attribute (UTF-8 SerString)'; Dll = 'sweep-attr' }
        @{ Kind = 'resx string resource (UTF-8 in .resources)'; Dll = 'sweep-resx' }
    ) {
        $report = Join-Path $TestDrive "report-$Dll.json"
        $r = Invoke-Scan $script:Dlls[$Dll] @('-ReportPath', $report, '-MaxFindingsPerFile', '5000', '-ShowMatchHashes')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $found = @((Get-Content -Raw $report | ConvertFrom-Json).findings | ForEach-Object matchSha256)
        $missing = @(for ($i = 0; $i -lt 200; $i++) { if ($found -notcontains (Get-Sha $script:SweepLiterals[$i])) { $i + 1 } })
        $missing | Should -BeNullOrEmpty -Because "every value length must be caught; missed value lengths: $($missing -join ',')"
    }

    It 'does not flag a compiled DLL whose literals only mention the keywords (no value)' {
        $r = Invoke-Scan $script:Dlls['clean']
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    It 'does not flag a compiled record with Password, Server and Database properties (its ToString text in the #US heap)' {
        $r = Invoke-Scan $script:Dlls['record']
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }
}
