<#
.SYNOPSIS
  Regenerates the README and Microsoft Store screenshots from the real demo dataset, through the real client and API.

.DESCRIPTION
  Nothing is drawn from made-up data. The script:

    1. Refuses to run when the SQL connection points at Azure SQL or carries a credential: it only ever uses a throw-away local database.
    2. Creates a fresh, uniquely named local database filled by the demo data generator (the same generator and size as the deployed demo, via
       tests/perf/Lifestream.Perf.Seed at factor 1), starts the real API over it on a free loopback port (hidden, no window), and runs the
       off-screen capture test (Category=Screenshots, tests/Lifestream.Desktop.Tests/EndToEnd/DemoScreenshotTests.cs) with LIFESTREAM_CAPTURE_DIR set to a
       temporary folder. That test opens the sign-in screen, presses "Try the demo" and opens every screen as the demo account; it fails (and writes no image of
       the screen) on an error or empty state, or when the dashboard counts differ from the demo dataset's. It also writes dataset.json.
       The API and the database are stopped and dropped at the end, whatever happened.
    3. Fails (exit 2) unless every expected screen produced a valid PNG of at least 1366x768 (the Store minimum), and dataset.json is there.
    4. Scans the images for credentials, copies them to docs/screenshots (README) and packaging/store/screenshots (Store) and writes screenshots.json
       (dataset, the dashboard counts shown, file, size, SHA-256) next to each copy.

  Contract for the capture test: it writes <screen>.png into LIFESTREAM_CAPTURE_DIR, one per expected screen, after loading the screen from the demo dataset
  through the real API, and dataset.json describing what the dashboard showed.

.PARAMETER CaptureScript  Path of a script that replaces the database, API and capture test (the Pester tests pass a fake that writes PNGs).
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$StoreDir,
    [string[]]$ExpectedScreens = @('login', 'dashboard', 'donors', 'patients', 'appointments', 'requests', 'stock', 'receipts', 'staff', 'profile'),
    [string]$CaptureScript,
    [string]$SqlConnection = $(if ($env:LIFESTREAM_TEST_SQL) { $env:LIFESTREAM_TEST_SQL } else { 'Server=localhost;Integrated Security=true;TrustServerCertificate=true' }),
    [int]$MinWidth = 1366,
    [int]$MinHeight = 768
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ExpectedScreens = @($ExpectedScreens | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # `-File x.ps1 -ExpectedScreens a,b` arrives as one string
if (-not $OutputDir) { $OutputDir = Join-Path $root 'docs' 'screenshots' }
if (-not $StoreDir) { $StoreDir = Join-Path $root 'packaging' 'store' 'screenshots' }

# 1. Demo data only: a local throw-away database, no Azure SQL, no credentials.
if ($SqlConnection -match '(?i)database\.windows\.net|\.sql\.azuresynapse|(?:password|pwd)\s*=|user\s+id\s*=') {
    Write-Host "ERROR: refusing to take screenshots against a real or credentialed database. Use the local test server (Integrated Security)."
    exit 2
}

function Stop-Quietly($process) { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }

$capture = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-shots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $capture | Out-Null
try {
    $env:LIFESTREAM_CAPTURE_DIR = $capture
    $env:LIFESTREAM_SCREENSHOT_DATASET = 'demo'
    $env:LIFESTREAM_TEST_SQL = $SqlConnection

    # 2. Capture.
    if ($CaptureScript) { & $CaptureScript }
    else {
        $env:DOTNET_NOLOGO = '1'
        foreach ($project in 'src/Lifestream.Api', 'tests/perf/Lifestream.Perf.Seed', 'tests/Lifestream.Desktop.Tests') {
            & dotnet build (Join-Path $root $project) -c Release --nologo -v quiet | Out-Host
            if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: build of $project failed"; exit 2 }
        }
        $seedDll = Join-Path $root 'tests/perf/Lifestream.Perf.Seed/bin/Release/net11.0/Lifestream.Perf.Seed.dll'
        $apiDll = Join-Path $root 'src/Lifestream.Api/bin/Release/net11.0/Lifestream.Api.dll'
        $dbName = 'Lifestream_Perf_' + [guid]::NewGuid().ToString('N')
        $today = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
        $created = $false
        $api = $null
        try {
            # The seed tool needs an administrator password for its measuring session; the screenshots sign in as the demo account, so it is random and never used.
            $env:LIFESTREAM_PERF_ADMIN_PASSWORD = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)) + 'aZ1!'
            $created = $true
            $out = & dotnet $seedDll create --factor 1 --today $today --name $dbName
            if ($LASTEXITCODE -ne 0) { $created = $false; Write-Host 'ERROR: creating the demo database failed'; exit 2 }
            $info = $out | Select-Object -Last 1 | ConvertFrom-Json
            Write-Host "demo database $dbName ($today): $($info.rows.Donor) donors, $($info.rows.Patient) patients, $($info.rows.Appointment) appointments, $($info.rows.Request) requests, $($info.rows.Receipt) receipts"

            $port = & { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
            $base = "http://127.0.0.1:$port/"
            $env:ASPNETCORE_URLS = $base.TrimEnd('/')
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:ConnectionStrings__Lifestream = "Server=localhost;Database=$dbName;Integrated Security=true;TrustServerCertificate=true"
            $apiLog = Join-Path $capture 'api.log'
            $api = Start-Process dotnet -ArgumentList "`"$apiDll`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiLog -RedirectStandardError "$apiLog.err"
            $up = $false
            foreach ($i in 1..60) {
                try { if ((Invoke-WebRequest "$($base)healthz" -TimeoutSec 2 -SkipHttpErrorCheck).StatusCode -eq 200) { $up = $true; break } } catch { }
                Start-Sleep -Milliseconds 1000
            }
            if (-not $up) { Write-Host "ERROR: NO SIGNAL: the API did not answer /healthz within 60 s on $base"; exit 2 }
            Write-Host "API on $base ready: $((Invoke-RestMethod "$($base)readyz" -TimeoutSec 30).database)"

            $env:LIFESTREAM_SHOTS_API = $base
            $env:LIFESTREAM_SHOTS_TODAY = $today
            & dotnet test (Join-Path $root 'tests' 'Lifestream.Desktop.Tests') -c Release --no-build --filter 'Category=Screenshots'
            if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: the capture test failed (exit $LASTEXITCODE)"; exit 2 }
        }
        finally {
            Stop-Quietly $api
            Remove-Item Env:LIFESTREAM_PERF_ADMIN_PASSWORD, Env:ConnectionStrings__Lifestream, Env:LIFESTREAM_SHOTS_API -ErrorAction SilentlyContinue
            if ($created) {
                Start-Sleep -Milliseconds 500
                & dotnet $seedDll drop --name $dbName | Out-Host
            }
        }
    }

    # 3. Every expected screen, a real PNG, big enough.
    Add-Type -AssemblyName System.Drawing
    $records = @()
    $problems = @()
    foreach ($screen in $ExpectedScreens) {
        $file = Join-Path $capture "$screen.png"
        if (-not (Test-Path $file)) { $problems += "missing screenshot for '$screen'"; continue }
        $bytes = [IO.File]::ReadAllBytes($file)
        if ($bytes.Length -lt 8 -or [BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { $problems += "$screen.png is not a PNG"; continue }
        # IHDR: width and height are big-endian 32-bit integers at offsets 16 and 20.
        $width = ([int]$bytes[16] -shl 24) + ([int]$bytes[17] -shl 16) + ([int]$bytes[18] -shl 8) + [int]$bytes[19]
        $height = ([int]$bytes[20] -shl 24) + ([int]$bytes[21] -shl 16) + ([int]$bytes[22] -shl 8) + [int]$bytes[23]
        if ($width -lt $MinWidth -or $height -lt $MinHeight) { $problems += "$screen.png is ${width}x${height}, smaller than ${MinWidth}x${MinHeight}"; continue }
        $records += [pscustomobject]@{
            screen = $screen; file = "$screen.png"; width = $width; height = $height
            sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
        }
    }
    $dataset = $null
    $datasetFile = Join-Path $capture 'dataset.json'
    if (Test-Path $datasetFile) { $dataset = Get-Content $datasetFile -Raw | ConvertFrom-Json }
    elseif (-not $CaptureScript) { $problems += 'the capture test wrote no dataset.json (what the dashboard showed)' }
    if ($problems) { $problems | ForEach-Object { Write-Host "ERROR: $_" }; exit 2 }

    # 4. Credential scan, then publish to both folders.
    & (Join-Path $PSScriptRoot 'scan-artifacts.ps1') -Path $capture | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: the screenshots failed the credential scan"; exit 1 }

    $manifest = [pscustomobject]@{ dataset = 'demo'; generatedBy = 'tools/generate-screenshots.ps1'; source = $(if ($dataset) { "real API over a fresh database filled by $($dataset.generator), signed in with Try the demo, data relative to $($dataset.today)" } else { 'fake capture' }); dashboard = $(if ($dataset) { $dataset.dashboard } else { $null }); files = $records }
    foreach ($target in $OutputDir, $StoreDir) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem $target -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
        foreach ($r in $records) { Copy-Item (Join-Path $capture $r.file) (Join-Path $target $r.file) -Force }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $target 'screenshots.json') -Encoding utf8
    }
    Write-Host "OK: $($records.Count) screenshots (dataset: demo) written to $OutputDir and $StoreDir"
}
finally {
    Remove-Item $capture -Recurse -Force -ErrorAction SilentlyContinue
}
