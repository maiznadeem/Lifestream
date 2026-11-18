<#
.SYNOPSIS  Builds an unsigned MSIX for the Microsoft Store from the published desktop app.
.DESCRIPTION
  Lays out the publish folder plus a generated AppxManifest.xml and logo assets, then runs makeappx. The Store signs the
  package on submission, so no certificate is needed here. Identity values come from the environment (the owner's Partner
  Center reservation): STORE_IDENTITY_NAME, STORE_PUBLISHER, STORE_PUBLISHER_DISPLAY_NAME.
.PARAMETER PublishDir  Output of `dotnet publish src/Lifestream.Desktop -r win-x64 --self-contained`.
.PARAMETER Version     Three-part version (2.0.0); the fourth part is always 0 as the Store requires.
.PARAMETER ManifestOnly  Writes the manifest and assets into -OutputDir\layout and stops (no makeappx). Used by the tests.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDir,
    [string]$IdentityName = $env:STORE_IDENTITY_NAME,
    [string]$Publisher = $env:STORE_PUBLISHER,
    [string]$PublisherDisplayName = $env:STORE_PUBLISHER_DISPLAY_NAME,
    [switch]$ManifestOnly
)

$ErrorActionPreference = 'Stop'
foreach ($required in 'IdentityName', 'Publisher', 'PublisherDisplayName') {
    if (-not (Get-Variable $required -ValueOnly)) { throw "$required is empty. Set STORE_IDENTITY_NAME, STORE_PUBLISHER and STORE_PUBLISHER_DISPLAY_NAME from Partner Center (packaging/store/README.md)." }
}
if (-not (Test-Path (Join-Path $PublishDir 'Lifestream.exe'))) { throw "Lifestream.exe not found in $PublishDir" }

$layout = Join-Path $OutputDir 'layout'
Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
Copy-Item (Join-Path $PublishDir '*') $layout -Recurse -Force

# The Store version is x.y.z.0; a pre-release suffix is not allowed in a manifest.
$numeric = ($Version -split '-')[0]
$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw
$manifest = $manifest.Replace('{{IDENTITY_NAME}}', [Security.SecurityElement]::Escape($IdentityName)).
    Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher)).
    Replace('{{PUBLISHER_DISPLAY_NAME}}', [Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace('{{VERSION}}', "$numeric.0")
Set-Content -Path (Join-Path $layout 'AppxManifest.xml') -Value $manifest -Encoding utf8

# Placeholder tile art in the brand red; the owner replaces these with final artwork (packaging/store/README.md).
Add-Type -AssemblyName System.Drawing
foreach ($asset in @{ 'StoreLogo.png' = 50; 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150 }.GetEnumerator()) {
    $custom = Join-Path $PSScriptRoot 'Assets' $asset.Key
    if (Test-Path $custom) { Copy-Item $custom (Join-Path $layout 'Assets' $asset.Key) -Force; continue }   # owner-supplied artwork wins
    $size = $asset.Value
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::FromArgb(0xC8, 0x10, 0x2E))
    $white = [System.Drawing.Brushes]::White
    $g.FillEllipse($white, [int]($size * 0.3), [int]($size * 0.3), [int]($size * 0.4), [int]($size * 0.4))
    $g.Dispose()
    $bmp.Save((Join-Path $layout 'Assets' $asset.Key), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

if ($ManifestOnly) { Write-Host "Layout written to $layout"; return }

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found; install the Windows 10/11 SDK (it is on the GitHub windows-latest runner)." }
$msix = Join-Path $OutputDir "Lifestream-$Version-x64.msix"
Remove-Item $msix -Force -ErrorAction SilentlyContinue
& $makeappx.FullName pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
Write-Host "Built $msix"
