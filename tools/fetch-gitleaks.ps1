<#
.SYNOPSIS
  Downloads the pinned gitleaks release into tools/.bin/ (never committed) and prints the path of the binary.

.DESCRIPTION
  The second scanner of tools/scan-artifacts.ps1 is gitleaks, a maintained scanner with its own rule set. The version is pinned here
  with the SHA-256 of each archive as published in the release's gitleaks_<version>_checksums.txt; an archive whose hash differs is
  deleted and the script fails (exit 2). Nothing is downloaded when the verified binary is already there. Windows x64 and Linux x64.
  Used by scan-artifacts.ps1 (automatically, when the binary is missing) and by CI and the infra tests.
#>
[CmdletBinding()]
param([string]$Directory = (Join-Path $PSScriptRoot '.bin'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '8.30.1'
# From https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_checksums.txt
$assets = @{
    windows = @{ File = "gitleaks_${version}_windows_x64.zip"; Sha256 = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'; Exe = 'gitleaks.exe' }
    linux   = @{ File = "gitleaks_${version}_linux_x64.tar.gz"; Sha256 = '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb'; Exe = 'gitleaks' }
}
$os = if ($IsLinux) { 'linux' } elseif ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows' } else { Write-Error "no pinned gitleaks for this OS"; exit 2 }
$a = $assets[$os]
$dir = Join-Path $Directory "gitleaks-$version"
$exe = Join-Path $dir $a.Exe
$marker = Join-Path $dir '.verified-sha256'
if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $a.Sha256)) { return $exe }

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$archive = Join-Path $dir $a.File
$url = "https://github.com/gitleaks/gitleaks/releases/download/v$version/$($a.File)"
Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 300
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $a.Sha256) {
    Remove-Item -LiteralPath $archive -Force
    Write-Host "ERROR: $($a.File) has SHA-256 $actual, expected $($a.Sha256) (the published checksum); not used."
    exit 2
}
if ($os -eq 'windows') { Expand-Archive -LiteralPath $archive -DestinationPath $dir -Force }
else { & tar -xzf $archive -C $dir; if ($LASTEXITCODE -ne 0) { Write-Host 'ERROR: tar failed'; exit 2 }; & chmod +x $exe }
Remove-Item -LiteralPath $archive -Force
if (-not (Test-Path -LiteralPath $exe)) { Write-Host "ERROR: $($a.Exe) not found after extracting"; exit 2 }
Set-Content -LiteralPath $marker -Value $a.Sha256
$exe
