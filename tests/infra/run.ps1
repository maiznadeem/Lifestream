<#
.SYNOPSIS  Runs every infra check: Bicep assertions, scanner tests, bootstrap script tests, actionlint (with shellcheck) on the workflows.
.EXAMPLE   pwsh tests/infra/run.ps1
.EXAMPLE   pwsh tests/infra/run.ps1 -Filter '*INFRA-04*'
Tools: Pester 5 or later (LIFESTREAM_PESTER_PATH, or an installed module), actionlint (LIFESTREAM_ACTIONLINT or PATH) and
optionally shellcheck (LIFESTREAM_SHELLCHECK_DIR or PATH), which actionlint uses on `run:` scripts.
Every workflow, ci.yml included, must pass actionlint (Workflows.Tests.ps1 runs it too).
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string]$PesterPath = $env:LIFESTREAM_PESTER_PATH,
    [string]$ActionlintPath = $env:LIFESTREAM_ACTIONLINT,
    [string]$ShellcheckDir = $env:LIFESTREAM_SHELLCHECK_DIR,
    [switch]$SkipActionlint
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

if ($PesterPath) { Import-Module (Join-Path $PesterPath 'Pester') -MinimumVersion 5.0 -Force } else { Import-Module Pester -MinimumVersion 5.0 -Force }

# The scanner compiles tools/scan-engine.cs on every start; the tests start it a few hundred times, so they share one compiled copy
# in a folder of this run only (removed at the end).
$engineCache = Join-Path ([IO.Path]::GetTempPath()) ('lifestream-scan-engine-' + [guid]::NewGuid().ToString('N'))
$env:LIFESTREAM_SCAN_ENGINE_CACHE = $engineCache

$cfg = New-PesterConfiguration
$cfg.Run.Path = @(Get-ChildItem $PSScriptRoot -Include 'BicepTests.ps1', '*.Tests.ps1' -File -Recurse | ForEach-Object FullName)
$cfg.Run.PassThru = $true
$cfg.Output.Verbosity = 'Detailed'
if ($Filter) { $cfg.Filter.FullName = $Filter }
try { $result = Invoke-Pester -Configuration $cfg }
finally { Remove-Item -LiteralPath $engineCache -Recurse -Force -ErrorAction SilentlyContinue; $env:LIFESTREAM_SCAN_ENGINE_CACHE = $null }

$failed = $result.FailedCount + $result.FailedBlocksCount + $result.FailedContainersCount
if (-not $SkipActionlint -and -not $Filter) {
    $actionlint = if ($ActionlintPath) { $ActionlintPath } else { (Get-Command actionlint -ErrorAction Stop).Source }
    if ($ShellcheckDir) { $env:PATH = "$ShellcheckDir$([IO.Path]::PathSeparator)$env:PATH" }
    $hasShellcheck = [bool](Get-Command shellcheck -ErrorAction SilentlyContinue)
    Write-Host "actionlint (shellcheck $(if ($hasShellcheck) { 'on' } else { 'NOT on PATH, run: scripts are not shell-checked' }))"
    $all = @(Get-ChildItem (Join-Path $root '.github' 'workflows') -Filter '*.yml' | ForEach-Object FullName)
    & $actionlint @all
    if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "actionlint FAILED" -ForegroundColor Red }
    else { Write-Host "actionlint passed on all $($all.Count) workflows (ci.yml included)" -ForegroundColor Green }
}
if ($failed -gt 0) { exit 1 }
