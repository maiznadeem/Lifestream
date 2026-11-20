# INFRA-01 against REAL artifacts, built here (Windows, .NET 11 SDK, vpk, network for the stock image layers):
#   - the Desktop app published the way release.yml does (win-x64, self-contained, ReadyToRun) from a copy of the tracked source,
#     once clean and once with the v-03c verifier's plant: one 65-byte connection string with a Pwd key as an assembly attribute and as
#     a XAML Tag (compiled to BAML), both stored as UTF-8 behind a length byte 'A';
#   - a Velopack Releases folder (full nupkg, Portable.zip, Setup.exe) packed from each publish;
#   - the API publish (linux-x64, framework-dependent, no apphost: as the Dockerfile and secret-scan.yml publish it);
#   - the stock mcr.microsoft.com/dotnet/aspnet:11.0 linux/amd64 layers, each layer blob scanned separately, as downloaded.
# Scanner v3 adds the verifier's v-03d counterexamples, each in the REAL publish and in the vpk Releases packed from it: a connection
# string in Lifestream.dll whose password value starts with a tilde, an appsettings.json password in braces, a shipped db.env whose
# password values start with ~ and >, and gzip, brotli and raw-deflate compressed embedded resources; plus a compressed single-file bundle.
# Both scanners run (as CI runs them); the clean artifacts must have zero findings from BOTH.
# Planted: every artifact must fail. Clean: zero findings. The scan of a full publish must take under 2 minutes.
# Missing tools fail the tests; they never skip. The plant is assembled from fragments so this file is clean when scanned.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Scanner = Join-Path $script:Root 'tools' 'scan-artifacts.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }
    $script:Val = 'Zq7x' + 'K2mP9'
    $plant = ('P' + 'wd=') + $script:Val + ';Server=tcp:x.example;Database=BloodBank;'
    $script:Plant = $plant + ('x' * (65 - $plant.Length - 1)) + ';'

    function script:Invoke-Scan([string[]]$Paths, [string]$Report, [string[]]$Extra = @()) {
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path ($Paths -join ',') -ReportPath $Report -MaxFindingsPerFile 200 @Extra 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out; Report = (Get-Content -Raw $Report | ConvertFrom-Json) }
    }

    $candidates = @($env:LIFESTREAM_DOTNET_ROOT, (Join-Path $script:Root '..' '..' 'tools' 'dotnet'), (Split-Path (Get-Command dotnet -ErrorAction SilentlyContinue).Source -ErrorAction SilentlyContinue)) |
        Where-Object { $_ -and (Test-Path (Join-Path $_ 'dotnet.exe')) }
    $script:DotnetDir = if ($candidates) { (Resolve-Path @($candidates)[0]).Path } else { $null }
    $vpks = @($env:LIFESTREAM_VPK, (Join-Path $script:Root '..' '..' 'tools' 'vpk.exe'), (Get-Command vpk -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) }
    $script:Vpk = if ($vpks) { (Resolve-Path @($vpks)[0]).Path } else { $null }

    # DOTNET_ROOT (the .NET 11 SDK) is set for dotnet only; vpk runs on the machine's own .NET runtime.
    function script:Start-Tool([string]$Exe, [string[]]$ToolArgs, [string]$Log, [switch]$NoDotnetRoot) {
        $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
        foreach ($a in $ToolArgs) { $psi.ArgumentList.Add($a) }
        if ($script:DotnetDir -and -not $NoDotnetRoot) {
            $psi.Environment['DOTNET_ROOT'] = $script:DotnetDir
            $psi.Environment['PATH'] = $script:DotnetDir + [IO.Path]::PathSeparator + $env:PATH
        }
        $psi.Environment['DOTNET_NOLOGO'] = '1'; $psi.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $p = [Diagnostics.Process]::Start($psi)
        [pscustomobject]@{ Process = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync(); Log = $Log; What = "$Exe $($ToolArgs -join ' ')" }
    }
    function script:Wait-Tool($t) {
        $t.Process.WaitForExit()
        Set-Content -LiteralPath $t.Log -Value ($t.Out.Result + $t.Err.Result)
        if ($t.Process.ExitCode -ne 0) { throw "failed ($($t.Process.ExitCode)): $($t.What)`n$($t.Out.Result)`n$($t.Err.Result)" }
    }

    # A copy of the tracked source needed to publish (src/ and the build files), so the plant never touches the worktree.
    function script:Copy-Source([string]$To) {
        $names = ((& git -C $script:Root ls-files -z -- src global.json Directory.Build.props Directory.Packages.props .editorconfig) -join '') -split "`0" | Where-Object { $_ }
        foreach ($n in $names) {
            $dest = Join-Path $To $n
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath (Join-Path $script:Root $n) -Destination $dest
        }
    }

    $script:Work = Join-Path $TestDrive 'real'
    $script:Timings = [ordered]@{}
    if ($script:DotnetDir -and $script:Vpk) {
        $clean = Join-Path $script:Work 'src-clean'; $planted = Join-Path $script:Work 'src-planted'
        Copy-Source $clean; Copy-Source $planted
        Set-Content -LiteralPath (Join-Path $planted 'src/Lifestream.Desktop/VerifierPlant.cs') -Encoding utf8 ('[assembly: System.Reflection.AssemblyMetadata("cs", "' + $script:Plant + '")]')
        $xaml = Join-Path $planted 'src/Lifestream.Desktop/Auth/LoginView.xaml'
        $lines = [Collections.Generic.List[string]](Get-Content -LiteralPath $xaml)
        # After the logo of the sign-in form (one self-closed element in a StackPanel; 2.0 had an Image there, the redesign a LogoMark).
        $at = ($lines.FindIndex({ param($l) $l -match '<controls:LogoMark [^>]*/>\s*$' }))
        if ($at -lt 0) { throw 'LoginView.xaml has no <controls:LogoMark ... /> line to plant after' }
        $lines.Insert($at + 1, '        <TextBlock Visibility="Collapsed" Tag="' + $script:Plant + '" />')
        Set-Content -LiteralPath $xaml -Value $lines -Encoding utf8

        # The v3 plants (fake values, assembled from fragments).
        $pw = 'Pass' + 'word'
        $script:V3 = [ordered]@{ A = 'Rt4w' + 'Lm8q' + 'Zc2X'; B = 'Hb7n' + 'Ue3k' + 'Wq9Y'; C = 'Gs5p' + 'Ji1v' + 'Nd6T'; D = 'Mf2c' + 'Ox8r' + 'Ka4E'; E = 'Yv3h' + 'Pe7t' + 'Lu1B' }
        $v3 = Join-Path $script:Work 'src-v3'; Copy-Source $v3
        $d3 = Join-Path $v3 'src/Lifestream.Desktop'
        Set-Content -LiteralPath (Join-Path $d3 'VerifierPlantV3.cs') -Encoding utf8 @"
namespace Lifestream.Desktop;
internal static class VerifierPlantV3
{
    internal static readonly string M1 = "Data Source=db.internal,1433;Initial Catalog=plantm1;$pw=~$($script:V3.A);Encrypt=True";
}
"@
        Set-Content -LiteralPath (Join-Path $d3 'db.env') -Encoding ascii -NoNewline ("DB_$($pw.ToUpper())=~$($script:V3.B)`nMSSQL_SA_$($pw.ToUpper())=>$($script:V3.B)`n")
        $app = Get-Content -Raw (Join-Path $d3 'appsettings.json')
        $app = $app.TrimEnd().TrimEnd('}') + ",`n  `"Database`": { `"Server`": `"db.internal`", `"User`": `"plantm2`", `"$pw`": `"{$($script:V3.B)}`" }`n}`n"
        Set-Content -LiteralPath (Join-Path $d3 'appsettings.json') -Value $app -Encoding utf8 -NoNewline
        $kinds = @(@('gzip', 'seed.bin.gz', 'C'), @('brotli', 'seed.bin.br', 'D'), @('deflate', 'seed.bin.deflate', 'E'))
        foreach ($k in $kinds) {
            $bytes = [Text.Encoding]::UTF8.GetBytes("Server=tcp:db.internal,1433;Database=plant$($k[0]);$('User' + ' ID')=app;$pw=$($script:V3[$k[2]]);")
            $ms = [IO.MemoryStream]::new()
            $z = switch ($k[0]) { 'gzip' { [IO.Compression.GZipStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) } 'brotli' { [IO.Compression.BrotliStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) } default { [IO.Compression.DeflateStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) } }
            $z.Write($bytes, 0, $bytes.Length); $z.Dispose()
            [IO.File]::WriteAllBytes((Join-Path $d3 $k[1]), $ms.ToArray())
        }
        $proj3 = Join-Path $d3 'Lifestream.Desktop.csproj'
        $x = Get-Content -Raw $proj3
        $needle = '<Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />'
        if (-not $x.Contains($needle)) { throw 'Lifestream.Desktop.csproj has no appsettings.json Content line to plant after' }
        $x = $x.Replace($needle, $needle + '<Content Include="db.env" CopyToOutputDirectory="PreserveNewest" /><EmbeddedResource Include="seed.bin.gz" /><EmbeddedResource Include="seed.bin.br" /><EmbeddedResource Include="seed.bin.deflate" />')
        Set-Content -LiteralPath $proj3 -Value $x -Encoding utf8 -NoNewline

        $dotnet = Join-Path $script:DotnetDir 'dotnet.exe'
        $publish = @('publish', 'src/Lifestream.Desktop', '-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishReadyToRun=true', '-p:Version=2.0.1', '--nologo')
        $script:PubClean = Join-Path $script:Work 'pub-clean'; $script:PubPlanted = Join-Path $script:Work 'pub-planted'; $script:PubApi = Join-Path $script:Work 'pub-api'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $t1 = Start-Tool $dotnet (@('publish', (Join-Path $clean 'src/Lifestream.Desktop')) + $publish[2..($publish.Count - 1)] + @('-o', $script:PubClean)) (Join-Path $script:Work 'pub-clean.log')
        $t2 = Start-Tool $dotnet (@('publish', (Join-Path $planted 'src/Lifestream.Desktop')) + $publish[2..($publish.Count - 1)] + @('-o', $script:PubPlanted)) (Join-Path $script:Work 'pub-planted.log')
        $script:PubV3 = Join-Path $script:Work 'pub-v3'
        $t4 = Start-Tool $dotnet (@('publish', (Join-Path $v3 'src/Lifestream.Desktop')) + $publish[2..($publish.Count - 1)] + @('-o', $script:PubV3)) (Join-Path $script:Work 'pub-v3.log')
        Wait-Tool $t1; Wait-Tool $t2; Wait-Tool $t4
        $t3 = Start-Tool $dotnet @('publish', (Join-Path $clean 'src/Lifestream.Api'), '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false', '-p:UseAppHost=false', '--nologo', '-o', $script:PubApi) (Join-Path $script:Work 'pub-api.log')
        Wait-Tool $t3
        $script:Timings.publishSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        $script:RelClean = Join-Path $script:Work 'Releases-clean'; $script:RelPlanted = Join-Path $script:Work 'Releases-planted'
        $script:RelV3 = Join-Path $script:Work 'Releases-v3'
        foreach ($pair in @(@($script:PubClean, $script:RelClean), @($script:PubPlanted, $script:RelPlanted), @($script:PubV3, $script:RelV3))) {
            Wait-Tool (Start-Tool $script:Vpk @('pack', '--packId', 'Lifestream', '--packVersion', '2.0.1', '--packDir', $pair[0], '--mainExe', 'Lifestream.exe', '--runtime', 'win-x64', '--outputDir', $pair[1]) ($pair[1] + '.log') -NoDotnetRoot)
        }
    }

    # Stock aspnet:11.0 linux/amd64: the layer blobs exactly as the registry serves them (tar.gz), plus the image config.
    # Cached by digest (LIFESTREAM_SCAN_CACHE or the temp folder); every blob is checked against its sha256 digest.
    $script:Layers = $null
    $cache = if ($env:LIFESTREAM_SCAN_CACHE) { $env:LIFESTREAM_SCAN_CACHE } else { Join-Path ([IO.Path]::GetTempPath()) 'lifestream-scan-cache' }
    $base = 'https://mcr.microsoft.com/v2/dotnet/aspnet'
    $accept = 'application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json'
    $index = Invoke-RestMethod -Uri "$base/manifests/11.0" -Headers @{ Accept = $accept } -TimeoutSec 120
    if ($index.manifests) {
        $m = @($index.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq 'amd64' })[0]
        $manifest = Invoke-RestMethod -Uri "$base/manifests/$($m.digest)" -Headers @{ Accept = $accept } -TimeoutSec 120
    } else { $manifest = $index }
    $script:LayerDir = Join-Path $cache ('aspnet-11.0-amd64-' + $manifest.config.digest.Substring(7, 16))
    New-Item -ItemType Directory -Force -Path $script:LayerDir | Out-Null
    $blobs = @(@{ Digest = $manifest.config.digest; Name = 'config.json' }) + @(for ($i = 0; $i -lt @($manifest.layers).Count; $i++) { @{ Digest = $manifest.layers[$i].digest; Name = "layer$i.tar.gz" } })
    foreach ($b in $blobs) {
        $f = Join-Path $script:LayerDir $b.Name
        $ok = (Test-Path $f) -and ('sha256:' + (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()) -eq $b.Digest
        if (-not $ok) {
            Invoke-WebRequest -Uri "$base/blobs/$($b.Digest)" -OutFile $f -TimeoutSec 600
            ('sha256:' + (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()) | Should -Be $b.Digest
        }
    }
    $script:LayerCount = @($manifest.layers).Count
}

AfterAll {
    # Evidence hook: LIFESTREAM_KEEP_V3_PLANTS=<folder> keeps the planted publish and its vpk Releases for a manual run of an older scanner.
    if ($env:LIFESTREAM_KEEP_V3_PLANTS -and $script:PubV3) {
        New-Item -ItemType Directory -Force -Path $env:LIFESTREAM_KEEP_V3_PLANTS | Out-Null
        Copy-Item -LiteralPath $script:PubV3 -Destination (Join-Path $env:LIFESTREAM_KEEP_V3_PLANTS 'pub-v3') -Recurse -Force
        Copy-Item -LiteralPath $script:RelV3 -Destination (Join-Path $env:LIFESTREAM_KEEP_V3_PLANTS 'Releases-v3') -Recurse -Force
    }
    if ($env:LIFESTREAM_CAPTURE_DIR -and $script:Timings.Count) {
        New-Item -ItemType Directory -Force -Path $env:LIFESTREAM_CAPTURE_DIR | Out-Null
        $script:Timings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:LIFESTREAM_CAPTURE_DIR 'scan-real-artifacts-timings.json')
    }
    Write-Host ('real-artifact scan timings: ' + ($script:Timings | ConvertTo-Json -Compress))
}

Describe 'INFRA-01 real artifacts: planted builds fail, clean builds and the stock image have zero findings' {
    It 'found the .NET SDK and vpk to build with' {
        $script:DotnetDir | Should -Not -BeNullOrEmpty -Because 'set LIFESTREAM_DOTNET_ROOT or put the SDK on PATH'
        $script:Vpk | Should -Not -BeNullOrEmpty -Because 'set LIFESTREAM_VPK or put vpk on PATH'
    }

    It 'the planted publish really holds the plant as UTF-8 behind its length byte "A", in Lifestream.dll (the fixture is real)' {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $script:PubPlanted 'Lifestream.dll'))
        $needle = [Convert]::ToHexString([byte[]]@(65) + [Text.Encoding]::UTF8.GetBytes($script:Plant))
        $hay = [Convert]::ToHexString($bytes)
        $first = $hay.IndexOf($needle)
        $first | Should -BeGreaterOrEqual 0
        $hay.IndexOf($needle, $first + 2) | Should -BeGreaterThan $first -Because 'the attribute blob and the BAML string each hold it'
    }

    It 'fails the planted Desktop publish, finding the plant in Lifestream.dll (raw bytes, the attribute and the BAML), and never prints it' {
        $r = Invoke-Scan @($script:PubPlanted) (Join-Path $TestDrive 'rep-pub-planted.json')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $own = @($r.Report.findings | Where-Object { $_.file -like '*\Lifestream.dll' })
        $own.Count | Should -BeGreaterThan 0 -Because $r.Output
        @($own.encoding) | Should -Contain 'UTF-8'
        @($own.encoding) | Should -Contain 'metadata:custom-attribute'
        @($own.encoding) | Should -Contain 'metadata:resource-stream'
        @($r.Report.findings | Where-Object { $_.file -notlike '*\Lifestream.dll' -and $_.file -notlike '*\Lifestream.dll!/(resource)' }) | Should -BeNullOrEmpty -Because 'only the plant is a credential (its own resource bytes are scanned under the label (resource))'
        $r.Output | Should -Not -Match $script:Val
    }

    It 'fails the vpk Releases folder packed from the planted publish: the full nupkg, Portable.zip and Setup.exe each' {
        $r = Invoke-Scan @($script:RelPlanted) (Join-Path $TestDrive 'rep-rel-planted.json')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        foreach ($container in '-full.nupkg!/', '-Portable.zip!/', '-Setup.exe!/') {
            @($r.Report.findings | Where-Object { $_.file -like "*$container*Lifestream.dll" }).Count | Should -BeGreaterThan 0 -Because "$container`n$($r.Output)"
        }
        $r.Output | Should -Not -Match $script:Val
    }

    It 'the v3 publish really holds each plant: UTF-16 string in Lifestream.dll, the json value, the env lines, and three compressed resources (the fixture is real)' {
        $hex = { param($f) [Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $script:PubV3 $f))) }
        $dll = & $hex 'Lifestream.dll'
        $dll.IndexOf([Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes('Pass' + 'word=~' + $script:V3.A))) | Should -BeGreaterOrEqual 0
        (Get-Content -Raw (Join-Path $script:PubV3 'appsettings.json')) | Should -Match ('"Pass' + 'word": "\{' + $script:V3.B + '\}"')
        (Get-Content -Raw (Join-Path $script:PubV3 'db.env')) | Should -Match ('DB_PASS' + 'WORD=~' + $script:V3.B)
        (Get-Content -Raw (Join-Path $script:PubV3 'db.env')) | Should -Match ('MSSQL_SA_PASS' + 'WORD=>' + $script:V3.B)
        foreach ($k in 'C', 'D', 'E') {
            $dll.IndexOf([Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($script:V3[$k]))) | Should -BeLessThan 0 -Because "resource $k is compressed, its value is not in the dll as plain bytes"
        }
    }

    It 'fails the v3 publish for each plant, from the own scanner alone: the dll string, appsettings.json, db.env and the gzip, brotli and deflate resources' {
        $r = Invoke-Scan @($script:PubV3) (Join-Path $TestDrive 'rep-pub-v3-own.json') @('-SkipGitleaks')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $files = @($r.Report.findings | ForEach-Object { $_.file })
        @($files | Where-Object { $_ -like '*\Lifestream.dll' }).Count | Should -BeGreaterThan 0 -Because $r.Output
        @($files | Where-Object { $_ -like '*\appsettings.json' }).Count | Should -BeGreaterThan 0 -Because $r.Output
        @($files | Where-Object { $_ -like '*\db.env' }).Count | Should -BeGreaterOrEqual 2 -Because 'the two env lines, one value starting with a tilde and one with a greater-than sign'
        foreach ($kind in '(gzip)', '(brotli)', '(deflate)') { @($files | Where-Object { $_ -like "*Lifestream*!/$kind" }).Count | Should -BeGreaterThan 0 -Because "$kind resource`n$($r.Output)" }
        $r.Output | Should -Not -Match ($script:V3.Values -join '|')
    }

    It 'fails the v3 publish from the second scanner alone as well: appsettings.json and db.env as files, the dll strings and the decompressed resources from the string dump' {
        $r = Invoke-Scan @($script:PubV3) (Join-Path $TestDrive 'rep-pub-v3-both.json')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $gl = @($r.Report.gitleaks.findings)
        @($gl | Where-Object { $_.source -eq 'artifact' -and $_.file -like '*appsettings.json' }).Count | Should -BeGreaterThan 0 -Because $r.Output
        @($gl | Where-Object { $_.source -eq 'artifact' -and $_.file -like '*db.env' }).Count | Should -BeGreaterThan 0 -Because $r.Output
        @($gl | Where-Object { $_.source -eq 'dump' -and $_.file -like '*Lifestream.dll' }).Count | Should -BeGreaterThan 0 -Because $r.Output
        foreach ($kind in '(gzip)', '(brotli)', '(deflate)') { @($gl | Where-Object { $_.source -eq 'dump' -and $_.file -like "*Lifestream*!/$kind" }).Count | Should -BeGreaterThan 0 -Because "$kind`n$($r.Output)" }
        $r.Output | Should -Not -Match ($script:V3.Values -join '|')
    }

    It 'fails the vpk Releases folder packed from the v3 publish: appsettings.json and db.env in the full nupkg, Portable.zip and Setup.exe each' {
        $r = Invoke-Scan @($script:RelV3) (Join-Path $TestDrive 'rep-rel-v3.json')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        foreach ($container in '-full.nupkg!/', '-Portable.zip!/', '-Setup.exe!/') {
            foreach ($f in 'appsettings.json', 'db.env', 'Lifestream.dll') {
                @($r.Report.findings | Where-Object { $_.file -like "*$container*$f" }).Count | Should -BeGreaterThan 0 -Because "$container $f`n$($r.Output)"
            }
        }
        $r.Output | Should -Not -Match ($script:V3.Values -join '|')
    }

    It 'fails a compressed single-file bundle (EnableCompressionInSingleFile): the plant is inside a deflate-compressed bundle entry' {
        $dir = Join-Path $script:Work 'bundle'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content (Join-Path $dir 'Bundle.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
        $pw = 'Pass' + 'word'
        Set-Content (Join-Path $dir 'Program.cs') ('System.Console.WriteLine("Server=tcp:db.internal,1433;Database=bundle;' + 'User' + ' ID=app;' + $pw + '=' + $script:V3.A + ';");')
        $out = Join-Path $script:Work 'bundle-out'
        Wait-Tool (Start-Tool (Join-Path $script:DotnetDir 'dotnet.exe') @('publish', $dir, '-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '--nologo', '-o', $out) (Join-Path $script:Work 'bundle.log'))
        (Get-Item (Join-Path $out 'Bundle.exe')).Length | Should -BeGreaterThan 10MB -Because 'a single file that holds the runtime'
        $hay = [Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $out 'Bundle.exe')))
        $hay.IndexOf([Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($script:V3.A))) | Should -BeLessThan 0 -Because 'the compressed bundle does not hold the value as plain bytes (so a raw scan cannot see it)'
        $r = Invoke-Scan @($out) (Join-Path $TestDrive 'rep-bundle.json') @('-SkipGitleaks')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        @($r.Report.findings | Where-Object { $_.file -like '*Bundle.exe!/Bundle.dll' }).Count | Should -BeGreaterThan 0 -Because $r.Output
    }

    It 'still flags a resource that happens to inflate to junk but holds a credential after its deflate stream (the original bytes are always scanned, the inflate is an extra)' {
        $dir = Join-Path $script:Work 'trail'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content (Join-Path $dir 'TrailLib.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><EmbeddedResource Include="trail.bin" /></ItemGroup></Project>'
        Set-Content (Join-Path $dir 'Class1.cs') 'namespace TrailLib { public class C { } }'
        $junk = [byte[]](1..96 | ForEach-Object { 0x41 + ($_ % 7) })
        $ms = [IO.MemoryStream]::new(); $z = [IO.Compression.DeflateStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true); $z.Write($junk, 0, $junk.Length); $z.Dispose()
        $tail = [Text.Encoding]::UTF8.GetBytes('Server=tcp:db.internal,1433;Database=trail;User ' + 'ID=app;Pass' + 'word=' + $script:V3.A + ';')
        $blob = $ms.ToArray() + $tail
        [IO.File]::WriteAllBytes((Join-Path $dir 'trail.bin'), $blob)
        # the premise: the blob inflates to 8 or more junk bytes (a raw deflate stream stops at its final block and ignores what follows)
        $back = [IO.MemoryStream]::new(); $d = [IO.Compression.DeflateStream]::new([IO.MemoryStream]::new($blob), [IO.Compression.CompressionMode]::Decompress); $d.CopyTo($back)
        $back.Length | Should -BeGreaterOrEqual 8
        $out = Join-Path $script:Work 'trail-out'
        Wait-Tool (Start-Tool (Join-Path $script:DotnetDir 'dotnet.exe') @('build', $dir, '-c', 'Release', '--nologo', '-o', $out) (Join-Path $script:Work 'trail.log'))
        $r = Invoke-Scan @($out) (Join-Path $TestDrive 'rep-trail.json') @('-SkipGitleaks')
        $r.ExitCode | Should -Be 1 -Because $r.Output
        @($r.Report.findings | Where-Object { $_.file -like '*TrailLib.dll!/(resource)' }).Count | Should -BeGreaterThan 0 -Because "the resource's own bytes must be scanned, not only its inflated junk`n$($r.Output)"
    }

    It 'has zero findings on the clean Desktop publish, and scans it in under 2 minutes' {
        $r = Invoke-Scan @($script:PubClean) (Join-Path $TestDrive 'rep-pub-clean.json')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        @($r.Report.findings).Count | Should -Be 0
        $r.Report.filesScanned | Should -BeGreaterThan 300
        $script:Timings.cleanPublishScanSeconds = $r.Report.seconds
        $script:Timings.cleanPublishBytes = $r.Report.bytesScanned
        $r.Report.seconds | Should -BeLessThan 120
    }

    It 'has zero findings on the clean vpk Releases folder (every member of the nupkg, the zip and Setup.exe opened)' {
        $r = Invoke-Scan @($script:RelClean) (Join-Path $TestDrive 'rep-rel-clean.json')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        @($r.Report.findings).Count | Should -Be 0
        $r.Report.filesScanned | Should -BeGreaterThan 900 -Because 'the nupkg, Portable.zip and Setup.exe each hold the whole app'
        $script:Timings.cleanReleasesScanSeconds = $r.Report.seconds
        $script:Timings.cleanReleasesBytes = $r.Report.bytesScanned
    }

    It 'has zero findings on the API publish, which holds the native libraries of linux-x64 only (what the image ships)' {
        @(Get-ChildItem $script:PubApi -Recurse -File | Where-Object { $_.Extension -in '.dylib', '.exe' -or $_.FullName -match '[\\/]runtimes[\\/](?!linux-x64[\\/])' }) | Should -BeNullOrEmpty -Because 'a publish without a runtime identifier copies macOS, Windows and musl libraries that are in no shipped artifact'
        $r = Invoke-Scan @($script:PubApi) (Join-Path $TestDrive 'rep-pub-api.json')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        @($r.Report.findings).Count | Should -Be 0
        $script:Timings.apiPublishScanSeconds = $r.Report.seconds
    }

    It 'has zero findings on the stock aspnet:11.0 layers, opening every layer separately, and every binary allow-list entry is still needed' {
        $r = Invoke-Scan @($script:LayerDir) (Join-Path $TestDrive 'rep-aspnet.json')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        @($r.Report.findings).Count | Should -Be 0
        $r.Report.filesScanned | Should -BeGreaterThan 5000
        $script:Timings.aspnetLayersScanSeconds = $r.Report.seconds
        $entries = Get-Content (Join-Path $script:Root 'tools' 'secret-scan-binary-allowlist.txt') | Where-Object { $_ -and -not $_.StartsWith('#') } |
            ForEach-Object { $n, $rule, $sha = $_ -split '\|'; '{0}|{1}|{2}' -f $n.ToLowerInvariant(), $rule, $sha }
        $unused = @($entries | Where-Object { @($r.Report.allowListEntriesUsed) -notcontains $_ })
        $unused | Should -BeNullOrEmpty -Because 'an entry that matches nothing in the stock layers is stale and must be removed'
    }

    It 'the same stock layers WITHOUT the allow-list do report the reviewed third-party strings (the allow-list is what clears them)' {
        $empty = Join-Path $TestDrive 'empty-allow.txt'; Set-Content -LiteralPath $empty '# empty'
        $r = Invoke-Scan @($script:LayerDir) (Join-Path $TestDrive 'rep-aspnet-noallow.json') @('-BinaryAllowlist', $empty)
        $r.ExitCode | Should -Be 1
        @($r.Report.findings | ForEach-Object { ($_.file -split '!/')[0] } | Sort-Object -Unique | Where-Object { $_ -like '*layer*.tar.gz' }).Count | Should -BeGreaterThan 1 -Because 'findings come from more than one layer blob'
    }
}
