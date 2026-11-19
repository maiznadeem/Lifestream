#Requires -Version 7
<#
.SYNOPSIS
  Proves the performance budgets of design section 5.5 against the real app, in one re-runnable script.

.DESCRIPTION
  Builds Release, then measures, in this order, and writes every figure with machine info to docs/perf/results.md:

    1. App launch to login window (published self-contained ReadyToRun exe, started with --perf-launch-pipe: the window is created far off the desktop, never
       activated, and reports its first rendered frame over a named pipe). Median of 10 cold-process runs after 1 warm-up. The script also checks that the
       foreground window never changed.
    2. Per fixture database (a uniquely named local SQL Server database filled by tests/perf/Lifestream.Perf.Seed from the deterministic ScaleDataGenerator,
       created and dropped by this script; the API runs over it as a separate process, started through the run launcher with --service and stopped at the end):
         1x   (demo size)  list endpoints and dashboard, p95 of 40 warm requests each
         10x  (110 staff)  staff list payload
         100x (27,000 receipts) list endpoints and dashboard; every screen's skeleton time, API calls, UI-thread gap over a 250 ms link and the 10,000-row grid
              (Category=Perf tests in tests/Lifestream.Desktop.Tests/EndToEnd); the first dashboard of a fresh process, median of 7 fresh processes
  Nothing is re-measured: each budget has one stated statistic over a stated sample, and the figures of the one run are what is reported.

  Run it on a QUIET machine: nothing else may be running. With -AssertBudgets the script refuses to start when the CPU is busy or another registered process is
  live, and exits 1 when any budget is missed, 2 when the machine was not quiet or something was broken (no signal), 0 when every budget is met.

.PARAMETER AssertBudgets  Exit non-zero on any miss, and refuse to measure on a busy machine.
.PARAMETER ResultsFile    Where the report goes (default docs/perf/results.md).
.PARAMETER Phases         Which parts to run: launch, 1x, 10x, 100x (default all).
.PARAMETER ColdRuns       Fresh processes for the cold dashboard (default 7, the statistic is their median).
.PARAMETER RunDir         The orchestrated run folder; when it and its launcher exist the API is started through the launcher.
#>
[CmdletBinding()]
param(
    [switch]$AssertBudgets,
    [string]$ResultsFile,
    [string[]]$Phases = @('launch', '1x', '10x', '100x'),
    [int]$ColdRuns = 7,
    [int]$LaunchRuns = 10,
    [string]$RunDir = 'G:/Claude/workspace/lifestream/run',
    [string]$Launcher = 'G:/Claude/config/plugins/cache/PersonalizedClaudeToolkit/coding-prefs/0.4.0/skills/orchestrated-run/scripts',
    [string]$WorkDir,
    [switch]$SkipQuietCheck,   # only for the planted-failure demonstrations: the failure they show does not depend on a quiet machine
    [string]$TestFilter   # development only: run this test filter instead of the phase's own
)

$ErrorActionPreference = 'Stop'
$Phases = @($Phases | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
if (-not $ResultsFile) { $ResultsFile = Join-Path $root 'docs' 'perf' 'results.md' }
if (-not $WorkDir) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-perf-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$jsonl = Join-Path $WorkDir 'measurements.jsonl'
New-Item -ItemType File -Force -Path $jsonl | Out-Null
$useLauncher = (Test-Path (Join-Path $Launcher 'runproc.py')) -and (Test-Path $RunDir)
$tag = (Get-Date -Format 'HHmmss')

function Fail([string]$message, [int]$code = 2) { Write-Host "ERROR: $message"; $script:exitCode = $code; throw [System.OperationCanceledException]::new($message) }

function Add-Record($budget, $name, $dataset, $stat, $n, $value, $unit, $limit, $pass, $detail = '') {
    $line = [ordered]@{ budget = $budget; name = $name; dataset = $dataset; stat = $stat; n = $n; value = [math]::Round([double]$value, 1); unit = $unit; limit = $limit; pass = [bool]$pass; detail = $detail } | ConvertTo-Json -Compress
    Add-Content -Path $jsonl -Value $line -Encoding utf8
}

# ---- machine and quiet check ----
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$machine = [ordered]@{
    cpu = "$($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) threads)"
    ram = "$([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB"
    os = "$($os.Caption) $($os.Version)"
    dotnet = (& dotnet --version)
    power = ((powercfg /getactivescheme) -join ' ') -replace '^.*\((.*)\).*$', '$1'
}
if ($machine.dotnet -notmatch '^11\.') { Write-Host "ERROR: dotnet $($machine.dotnet) found; the .NET 11 SDK must be first on PATH."; exit 2 }

function Test-Quiet {
    $busy = @()
    $samples = 1..5 | ForEach-Object { (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples[0].CookedValue; Start-Sleep -Milliseconds 400 }
    $avg = ($samples | Measure-Object -Average).Average
    if ($avg -gt 25) { $busy += "CPU is $([math]::Round($avg))% busy (limit 25%)" }
    if ($useLauncher) {
        $others = (& python (Join-Path $Launcher 'killproc.py') --run $RunDir --list) | Where-Object { $_ -match 'live=yes' -and $_ -notmatch '^monitor ' -and $_ -notmatch '^b-10-perf' }
        foreach ($o in $others) { $busy += "another registered process is live: $(($o -split ' ')[0])" }
    }
    return $busy
}

$cleanup = New-Object System.Collections.Generic.List[scriptblock]
$exitCode = 0
$seedDll = $null
try {
    if ($AssertBudgets -and -not $SkipQuietCheck) {
        $busy = Test-Quiet
        if ($busy) { Fail ("the machine is not quiet: " + ($busy -join '; ') + ". Run this on a quiet machine (nothing else running).") 2 }
    }

    # ---- build ----
    Write-Host '== build =='
    foreach ($project in @('src/Lifestream.Api', 'tests/perf/Lifestream.Perf.Seed', 'tests/Lifestream.Desktop.Tests')) {
        & dotnet build (Join-Path $root $project) -c Release --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "build of $project failed" }
    }
    $seedDll = Join-Path $root 'tests/perf/Lifestream.Perf.Seed/bin/Release/net11.0/Lifestream.Perf.Seed.dll'
    $apiDll = Join-Path $root 'src/Lifestream.Api/bin/Release/net11.0/Lifestream.Api.dll'

    # ---- 1. launch ----
    if ($Phases -contains 'launch') {
        Write-Host '== app launch to login window =='
        $publish = Join-Path $WorkDir 'publish'
        & dotnet publish (Join-Path $root 'src/Lifestream.Desktop/Lifestream.Desktop.csproj') -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $publish --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'publish of the desktop app failed' }
        $exe = Join-Path $publish 'Lifestream.exe'
        Add-Type -Namespace Win -Name Fg -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();'

        function Measure-Launch {
            $pipeName = 'lifestream-perf-' + [guid]::NewGuid().ToString('N')
            $pipe = [System.IO.Pipes.NamedPipeServerStream]::new($pipeName, [System.IO.Pipes.PipeDirection]::In, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
            try {
                $before = [Win.Fg]::GetForegroundWindow()
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                $psi = [System.Diagnostics.ProcessStartInfo]::new($exe, "--perf-launch-pipe $pipeName")
                $psi.UseShellExecute = $false
                $psi.WorkingDirectory = $publish   # the app reads appsettings.json from the current directory (see the report: it should use the app folder)
                $process = [System.Diagnostics.Process]::Start($psi)
                $connected = $pipe.WaitForConnectionAsync().Wait(15000)
                if (-not $connected) { $process.Kill($true); throw 'NO SIGNAL: the login window did not report within 15 s' }
                $reader = [System.IO.StreamReader]::new($pipe)
                $line = $reader.ReadLine()
                $clock.Stop()
                $after = [Win.Fg]::GetForegroundWindow()
                if (-not $process.WaitForExit(10000)) { $process.Kill($true) }
                if ($line -notmatch '^rendered ') { throw "unexpected report '$line'" }
                [pscustomobject]@{ WallMs = $clock.Elapsed.TotalMilliseconds; InAppMs = [double]($line -replace '^rendered ', ''); FocusChanged = ($before -ne $after) }
            }
            finally { $pipe.Dispose() }
        }

        $null = Measure-Launch   # the one warm-up
        $runs = 1..$LaunchRuns | ForEach-Object { Measure-Launch }
        $wall = @($runs | ForEach-Object { $_.WallMs } | Sort-Object)
        $median = if ($wall.Count % 2) { $wall[[int][math]::Floor($wall.Count / 2)] } else { ($wall[$wall.Count / 2 - 1] + $wall[$wall.Count / 2]) / 2 }
        $focus = @($runs | Where-Object FocusChanged).Count
        $detail = "runs (ms): $(($wall | ForEach-Object { [math]::Round($_) }) -join ', '); in-app time from process start to first frame, median $([math]::Round((@($runs.InAppMs | Sort-Object))[[int][math]::Floor($runs.Count / 2)])) ms; foreground window changed in $focus of $($runs.Count) runs; window created at -20000,-20000 not activated, not in the task bar"
        Add-Record 'launch' 'App launch to login window rendered (self-contained ReadyToRun, Release)' 'n/a' "median of $LaunchRuns cold-process runs after 1 warm-up" $LaunchRuns $median 'ms' '< 1000' (($median -lt 1000) -and $focus -eq 0) $detail
    }

    # ---- fixture databases, API, measurements ----
    function Invoke-Phase([string]$label, [int]$factor, [string[]]$filters, [int]$coldRuns = 0) {
        Write-Host "== $label fixture =="
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)) + 'aZ1!'
        $dbName = 'Lifestream_Perf_' + [guid]::NewGuid().ToString('N')
        $env:LIFESTREAM_PERF_ADMIN_PASSWORD = $password
        $created = $false
        $serviceName = "b-10-perf-api-$label-$tag"
        $apiProcess = $null
        try {
            $created = $true
            $out = & dotnet $seedDll create --factor $factor --name $dbName
            if ($LASTEXITCODE -ne 0) { $created = $false; Fail "creating the $label fixture failed" }
            $info = $out | Select-Object -Last 1 | ConvertFrom-Json
            Write-Host "   $dbName : $($info.rows.Receipt) receipts, $($info.rows.Appointment) appointments, $($info.rows.Request) requests, $($info.rows.Donor) donors, $($info.rows.Patient) patients, $($info.rows.Staff) staff ($($info.server))"
            # The generator is deterministic and exactly proportional (11 staff, 40 donors, 35 patients, 300 appointments, 150 requests, 270 receipts per 1x).
            $sizeOk = ($info.rows.Receipt -eq 270 * $factor) -and ($info.rows.Appointment -eq 300 * $factor) -and ($info.rows.Request -eq 150 * $factor) -and ($info.rows.Donor -eq 40 * $factor) -and ($info.rows.Patient -eq 35 * $factor) -and ($info.rows.Staff -eq 11 * $factor)
            Add-Record 'fixture' "Fixture database $label" $label 'rows created by Lifestream.Perf.Seed' 1 $info.rows.Receipt 'receipts' "$($info.rows | ConvertTo-Json -Compress)" $sizeOk "$($info.server); admin session user is a generated administrator; index rebuild and full-scan statistics done"

            # The API: a separate process over this database, through the run launcher with --service when there is one.
            $port = & { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
            $base = "http://127.0.0.1:$port/"
            $env:ASPNETCORE_URLS = $base.TrimEnd('/')
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:ConnectionStrings__Lifestream = "Server=localhost;Database=$dbName;Integrated Security=true;TrustServerCertificate=true"
            $env:Dashboard__CacheSeconds = '0'          # the budget says no cache is needed
            $env:Limits__GlobalPerMinute = '1000000'    # the timing loops send thousands of requests a minute
            $env:Limits__LoginPerMinute = '1000'
            if ($useLauncher) {
                & python (Join-Path $Launcher 'runproc.py') --run $RunDir --name $serviceName --service --expect-seconds 1800 -- dotnet $apiDll | Out-Null
            }
            else {
                $apiProcess = Start-Process dotnet -ArgumentList "`"$apiDll`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $WorkDir "api-$label.log")
            }
            $up = $false
            foreach ($i in 1..60) {
                try { if ((Invoke-WebRequest "$($base)healthz" -TimeoutSec 2 -SkipHttpErrorCheck).StatusCode -eq 200) { $up = $true; break } } catch { }
                Start-Sleep -Milliseconds 1000
            }
            if (-not $up) { Fail "NO SIGNAL: the API did not answer /healthz within 60 s on $base" }
            $ready = Invoke-RestMethod "$($base)readyz" -TimeoutSec 30
            Write-Host "   API on $base ready: $($ready.database)"

            $env:LIFESTREAM_PERF_API = $base
            $env:LIFESTREAM_PERF_USER = $info.admin
            $env:LIFESTREAM_PERF_PASSWORD = $password
            $env:LIFESTREAM_PERF_LABEL = $label
            $env:LIFESTREAM_PERF_RECEIPTS = "$($info.rows.Receipt)"
            $env:LIFESTREAM_PERF_RESULTS = $jsonl
            function Invoke-Perf([string]$phase) {
                # The tests run from the self-contained ReadyToRun publish, as the shipped app does: the JIT cost of a Debug-like test host is not what a user pays.
                & $testExe -trait "PerfPhase=$phase" -noColor | Select-Object -Last 45 | Out-Host
                return $LASTEXITCODE
            }
            $phaseNames = @($filters | ForEach-Object { $_ -replace '^.*PerfPhase=', '' })
            if ($TestFilter) { $phaseNames = @($TestFilter -replace '^.*PerfPhase=', ''); $coldRuns = 0 }
            foreach ($phase in $phaseNames) {
                Write-Host "   tests: PerfPhase=$phase"
                $code = Invoke-Perf $phase
                if ($code -ne 0) { Write-Host "   (PerfPhase=$phase exited ${code}: the misses are in the report)"; $script:testFailures += $phase }
            }
            for ($i = 1; $i -le $coldRuns; $i++) {
                Write-Host "   cold dashboard run $i of $coldRuns"
                $code = Invoke-Perf 'cold'
                if ($code -ne 0) { $script:testFailures += "cold run $i" }
            }
        }
        finally {
            if ($useLauncher) { & python (Join-Path $Launcher 'killproc.py') --run $RunDir $serviceName | Out-Host }
            elseif ($apiProcess) { Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue }
            Remove-Item Env:LIFESTREAM_PERF_PASSWORD, Env:LIFESTREAM_PERF_ADMIN_PASSWORD, Env:ConnectionStrings__Lifestream -ErrorAction SilentlyContinue
            if ($created) {
                Start-Sleep -Milliseconds 500
                & dotnet $seedDll drop --name $dbName | Out-Host
            }
        }
    }

    $script:testFailures = @()
    Write-Host '== publish the perf tests (self-contained, ReadyToRun) =='
    $testPublish = Join-Path $root 'tests/Lifestream.Desktop.Tests/bin/perf-r2r'
    & dotnet publish (Join-Path $root 'tests/Lifestream.Desktop.Tests') -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $testPublish --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'publish of the perf tests failed' }
    $script:testExe = Join-Path $testPublish 'Lifestream.Desktop.Tests.exe'
    if ($Phases -contains '1x') { Invoke-Phase '1x' 1 @('Category=Perf&PerfPhase=lists') }
    if ($Phases -contains '10x') { Invoke-Phase '10x' 10 @('Category=Perf&PerfPhase=staff') }
    if ($Phases -contains '100x') { Invoke-Phase '100x' 100 @('Category=Perf&PerfPhase=lists', 'Category=Perf&PerfPhase=screens') $ColdRuns }
}
catch [System.OperationCanceledException] { }
catch { Write-Host "ERROR: $($_.Exception.Message)"; $exitCode = 2 }
finally {
    if ($seedDll) {
        Write-Host '== fixture databases left on the server =='
        & dotnet $seedDll list | Out-Host
    }
}
if ($script:exitCode) { $exitCode = $script:exitCode }

# ---- report ----
$records = @(Get-Content $jsonl -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
# The cold dashboard is judged on the median of its fresh-process runs, not on any one run.
$cold = @($records | Where-Object { $_.budget -eq 'cold-dashboard' })
if ($cold.Count) {
    $values = @($cold.value | Sort-Object)
    $med = if ($values.Count % 2) { $values[[int][math]::Floor($values.Count / 2)] } else { ($values[$values.Count / 2 - 1] + $values[$values.Count / 2]) / 2 }
    $records = @($records | Where-Object { $_.budget -ne 'cold-dashboard' })
    $records += [pscustomobject]@{ budget = 'cold-dashboard'; name = 'Dashboard first chart render after the data arrives, fresh process'; dataset = '100x'; stat = "median of $($cold.Count) fresh processes"; n = $cold.Count; value = [math]::Round($med, 1); unit = 'ms'; limit = '< 300'; pass = ($med -lt 300 -and $cold.Count -ge $ColdRuns); detail = "runs (ms): $(($cold | ForEach-Object { $_.value }) -join ', '); max $(($values | Select-Object -Last 1)) ms" }
}
$expected = @()
if ($Phases -contains 'launch') { $expected += 'launch' }
if ($Phases -contains '1x' -or $Phases -contains '100x') { $expected += 'lists', 'dashboard' }
if ($Phases -contains '10x') { $expected += 'staff-payload' }
if ($Phases -contains '100x') { $expected += 'skeleton', 'api-calls', 'ui-gap', 'grid', 'cold-dashboard' }
$missing = @($expected | Select-Object -Unique | Where-Object { $b = $_; -not ($records | Where-Object { $_.budget -eq $b }) })
foreach ($m in $missing) { Write-Host "NO SIGNAL: no measurement was recorded for '$m'"; $exitCode = 2 }
$misses = @($records | Where-Object { -not $_.pass })

$labels = @{
    launch = 'App launch to login window (warm OS cache, Release, self-contained ReadyToRun)'
    lists = 'Warm API list endpoints (local SQL Server)'
    dashboard = 'Dashboard endpoint, no cache'
    skeleton = 'Any screen: skeleton visible after navigation'
    'api-calls' = 'API calls per screen load'
    'ui-gap' = 'UI thread gap during a load over a throttled link (250 ms latency)'
    'staff-payload' = 'Staff list payload for 110 staff'
    grid = 'Grid with 10,000 items'
    'cold-dashboard' = 'First (cold, fresh process) dashboard chart render after data arrives'
}
$limits = @{ launch = '< 1.0 s'; lists = 'p95 < 100 ms'; dashboard = 'p95 < 100 ms'; skeleton = '< 100 ms'; 'api-calls' = 'exactly 1 per data set'; 'ui-gap' = '< 50 ms'; 'staff-payload' = '< 20 KB, no image bytes'; grid = '< 300 row containers'; 'cold-dashboard' = '< 300 ms' }
$md = New-Object System.Text.StringBuilder
function Line($text = '') { [void]$md.AppendLine($text) }
Line '# Performance results'
Line
Line "Produced by ``tests/perf/run-perf.ps1$(if ($AssertBudgets) { ' -AssertBudgets' })`` on $(Get-Date -Format 'yyyy-MM-dd HH:mm') ($([TimeZoneInfo]::Local.StandardName), UTC$((Get-Date).ToString('zzz'))). Budgets are design section 5.5; each is asserted with one stated statistic over a stated sample, never a best-of re-measure."
Line
Line '| | |'
Line '|---|---|'
Line "| CPU | $($machine.cpu) |"
Line "| Memory | $($machine.ram) |"
Line "| OS | $($machine.os) |"
Line "| .NET SDK | $($machine.dotnet) |"
Line "| Power plan | $($machine.power) |"
$server = ($records | Where-Object { $_.budget -eq 'fixture' } | Select-Object -First 1).detail -replace ';.*$', ''
Line "| SQL Server | $server |"
Line "| Quiet check | $(if ($SkipQuietCheck) { 'SKIPPED (planted-failure demonstration)' } elseif ($AssertBudgets) { 'CPU under 25% and no other registered process live before the run' } else { 'not asserted (run without -AssertBudgets)' }) |"
Line
Line "**Verdict: $(if ($misses.Count -eq 0 -and $missing.Count -eq 0 -and -not $script:testFailures) { 'every budget met' } else { "$($misses.Count) measurement(s) missed" })**"
Line
Line '## Summary'
Line
Line '| Budget | Limit | Measured | Statistic | Verdict |'
Line '|---|---|---|---|---|'
foreach ($b in 'launch', 'skeleton', 'lists', 'dashboard', 'api-calls', 'ui-gap', 'staff-payload', 'grid', 'cold-dashboard') {
    $rows = @($records | Where-Object { $_.budget -eq $b })
    if (-not $rows.Count) { continue }
    $bad = @($rows | Where-Object { -not $_.pass }).Count
    $values = @($rows | ForEach-Object { $_.value })
    $unit = $rows[0].unit
    $summary = if ($b -in 'lists', 'dashboard', 'skeleton', 'ui-gap') { "worst $([math]::Round(($values | Measure-Object -Maximum).Maximum, 1)) $unit over $($rows.Count) measurement(s)" } elseif ($b -eq 'api-calls') { "$($rows.Count) screens/checks, $bad off" } else { "$([math]::Round($rows[0].value, 1)) $unit" }
    $stat = if ($b -eq 'lists' -or $b -eq 'dashboard') { 'p95 of 40 warm requests' } else { $rows[0].stat }
    Line "| $($labels[$b]) | $($limits[$b]) | $summary | $stat | $(if ($bad) { "MISSED ($bad)" } else { 'met' }) |"
}
Line
Line '## Every measurement'
Line
Line '| Budget | Dataset | Measurement | Statistic | Value | Limit | Verdict | Detail |'
Line '|---|---|---|---|---|---|---|---|'
foreach ($r in ($records | Where-Object { $_.budget -ne 'fixture' })) {
    Line "| $($r.budget) | $($r.dataset) | $($r.name) | $($r.stat) | $($r.value) $($r.unit) | $($r.limit) | $(if ($r.pass) { 'met' } else { '**MISSED**' }) | $($r.detail -replace '\|', '/') |"
}
Line
Line '## Fixture databases'
Line
foreach ($r in ($records | Where-Object { $_.budget -eq 'fixture' })) { Line "- **$($r.dataset)**: $($r.limit) ($($r.detail))" }
Line
Line '## How it is measured'
Line
Line '- **Launch**: the published exe is started with `--perf-launch-pipe <name>`; the login window is created at (-20000, -20000), never activated and kept out of the task bar, writes one line to the named pipe when its first frame has rendered, and the process exits. The time is the script''s stopwatch from just before process creation to that line. The foreground window is compared before and after every run.'
Line '- **API timings** are round trips over loopback HTTP from request sent to body read (the HTTP client and socket are included, so they are an upper bound of the server''s own time), 8 warm-up requests and then 40 timed; p95 is the 38th of 40 sorted samples. The API is the real Release build in its own process over a real local SQL Server database; the dashboard cache is off.'
Line '- **Screen measurements** use the real client (real HTTP client, wake-aware handler, session, navigation, view models, views) hosted off-screen over a real socket to that API. The skeleton time runs from the navigation command until the dispatcher has finished the layout pass that puts the skeleton on screen (the final present to the screen adds at most one frame). The UI-thread gap is the longest an input-priority message posted from another thread waited for the UI thread while a screen loaded over a 250 ms-per-request link, first visits included. The API-call count includes retries and lazy requests that land within 400 ms of the load.'
Line '- **Cold dashboard**: a fresh process each time (`PerfPhase=cold`): warm-up as at app start, sign-in, then the dashboard is loaded; the clock starts when the response body is fully in memory and stops when a rendered bitmap of the page shows both chart series (each look renders the page, which is part of the time).'
Line '- Nothing is shown on the desktop and no focus is taken: every view is hosted in a window whose parent is a message-only window, and the launch window is off-screen and not activated.'
$null = New-Item -ItemType Directory -Force -Path (Split-Path $ResultsFile)
Set-Content -Path $ResultsFile -Value $md.ToString() -Encoding utf8

Write-Host
foreach ($r in $misses) { Write-Host "MISSED: [$($r.budget)] $($r.name) ($($r.dataset)): $($r.value) $($r.unit), limit $($r.limit) - $($r.detail)" }
if ($script:testFailures) { Write-Host "test runs with failures: $($script:testFailures -join '; ')" }
Write-Host "report: $ResultsFile"
if ($exitCode -eq 2) { Write-Host 'RESULT: NO SIGNAL or broken (exit 2)'; exit 2 }
if ($misses.Count -or $missing.Count) {
    Write-Host "RESULT: $($misses.Count) budget measurement(s) MISSED"
    if ($AssertBudgets) { exit 1 }
    exit 0
}
Write-Host 'RESULT: every budget met'
exit 0
