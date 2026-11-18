<#
.SYNOPSIS
  Regenerates the README and Microsoft Store screenshots from the demo dataset only.

.DESCRIPTION
  Runs the off-screen capture tests (no window is ever shown), then checks, copies and records what they produced:

    1. Refuses to run when the SQL connection points at Azure SQL or carries a credential: the capture tests must use a
       throw-away local database filled by the demo data generator, never real data.
    2. Runs the capture command (default: dotnet test tests/Lifestream.Desktop.Tests --filter Category=Screenshots) with
       LIFESTREAM_CAPTURE_DIR set to a temporary folder and LIFESTREAM_SCREENSHOT_DATASET=demo.
    3. Fails (exit 2) unless every expected screen produced a valid PNG of at least 1366x768 (the Store minimum).
    4. Scans the images for credentials, copies them to docs/screenshots (README) and packaging/store/screenshots (Store)
       and writes screenshots.json (dataset, file, size, SHA-256) next to each copy.

  Contract for the capture tests: a test tagged Category=Screenshots writes <screen>.png into LIFESTREAM_CAPTURE_DIR, one per
  expected screen, after loading the screen from the demo dataset.

.PARAMETER CaptureScript  Path of a script that replaces the default dotnet test run (the Pester tests pass a fake that writes PNGs).
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$StoreDir,
    [string[]]$ExpectedScreens = @('login', 'dashboard', 'donors', 'patients', 'appointments', 'requests', 'stock', 'receipts', 'staff'),
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

$capture = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-shots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $capture | Out-Null
try {
    $env:LIFESTREAM_CAPTURE_DIR = $capture
    $env:LIFESTREAM_SCREENSHOT_DATASET = 'demo'
    $env:LIFESTREAM_TEST_SQL = $SqlConnection

    # 2. Capture.
    if ($CaptureScript) { & $CaptureScript }
    else {
        & dotnet test (Join-Path $root 'tests' 'Lifestream.Desktop.Tests') -c Release --filter 'Category=Screenshots'
        if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: the capture tests failed (exit $LASTEXITCODE)"; exit 2 }
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
    if ($problems) { $problems | ForEach-Object { Write-Host "ERROR: $_" }; exit 2 }

    # 4. Credential scan, then publish to both folders.
    & (Join-Path $PSScriptRoot 'scan-artifacts.ps1') -Path $capture | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: the screenshots failed the credential scan"; exit 1 }

    $manifest = [pscustomobject]@{ dataset = 'demo'; generatedBy = 'tools/generate-screenshots.ps1'; files = $records }
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
