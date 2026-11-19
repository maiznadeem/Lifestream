# INFRA-01 against REAL artifacts, built here (Windows, .NET 11 SDK, vpk, network for the stock image layers):
#   - the Desktop app published the way release.yml does (win-x64, self-contained, ReadyToRun) from a copy of the tracked source,
#     once clean and once with the v-03c verifier's plant: one 65-byte connection string with a Pwd key as an assembly attribute and as
#     a XAML Tag (compiled to BAML), both stored as UTF-8 behind a length byte 'A';
#   - a Velopack Releases folder (full nupkg, Portable.zip, Setup.exe) packed from each publish;
#   - the API publish (framework-dependent, as secret-scan.yml);
#   - the stock mcr.microsoft.com/dotnet/aspnet:11.0 linux/amd64 layers, each layer blob scanned separately, as downloaded.
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
        $at = ($lines.FindIndex({ param($l) $l -match 'Image\.Logo' }))
        if ($at -lt 0) { throw 'LoginView.xaml has no Image.Logo line to plant after' }
        $lines.Insert($at + 1, '        <TextBlock Visibility="Collapsed" Tag="' + $script:Plant + '" />')
        Set-Content -LiteralPath $xaml -Value $lines -Encoding utf8

        $dotnet = Join-Path $script:DotnetDir 'dotnet.exe'
        $publish = @('publish', 'src/Lifestream.Desktop', '-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishReadyToRun=true', '-p:Version=2.0.1', '--nologo')
        $script:PubClean = Join-Path $script:Work 'pub-clean'; $script:PubPlanted = Join-Path $script:Work 'pub-planted'; $script:PubApi = Join-Path $script:Work 'pub-api'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $t1 = Start-Tool $dotnet (@('publish', (Join-Path $clean 'src/Lifestream.Desktop')) + $publish[2..($publish.Count - 1)] + @('-o', $script:PubClean)) (Join-Path $script:Work 'pub-clean.log')
        $t2 = Start-Tool $dotnet (@('publish', (Join-Path $planted 'src/Lifestream.Desktop')) + $publish[2..($publish.Count - 1)] + @('-o', $script:PubPlanted)) (Join-Path $script:Work 'pub-planted.log')
        Wait-Tool $t1; Wait-Tool $t2
        $t3 = Start-Tool $dotnet @('publish', (Join-Path $clean 'src/Lifestream.Api'), '-c', 'Release', '--nologo', '-o', $script:PubApi) (Join-Path $script:Work 'pub-api.log')
        Wait-Tool $t3
        $script:Timings.publishSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        $script:RelClean = Join-Path $script:Work 'Releases-clean'; $script:RelPlanted = Join-Path $script:Work 'Releases-planted'
        foreach ($pair in @(@($script:PubClean, $script:RelClean), @($script:PubPlanted, $script:RelPlanted))) {
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
        @($r.Report.findings | Where-Object { $_.file -notlike '*\Lifestream.dll' }) | Should -BeNullOrEmpty -Because 'only the plant is a credential'
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

    It 'has zero findings on the API publish' {
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
