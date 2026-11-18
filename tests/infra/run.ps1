<#
.SYNOPSIS  Runs every infra check: Bicep assertions, scanner tests, bootstrap script tests, actionlint (with shellcheck) on the workflows.
.EXAMPLE   pwsh tests/infra/run.ps1
.EXAMPLE   pwsh tests/infra/run.ps1 -Filter '*INFRA-04*'
Tools: Pester 5 or later (LIFESTREAM_PESTER_PATH, or an installed module), actionlint (LIFESTREAM_ACTIONLINT or PATH) and
optionally shellcheck (LIFESTREAM_SHELLCHECK_DIR or PATH), which actionlint uses on `run:` scripts.
ci.yml belongs to the skeleton module; it is linted too, but its result is reported separately and does not fail this script.
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

$cfg = New-PesterConfiguration
$cfg.Run.Path = @(Get-ChildItem $PSScriptRoot -Include 'BicepTests.ps1', '*.Tests.ps1' -File -Recurse | ForEach-Object FullName)
$cfg.Run.PassThru = $true
$cfg.Output.Verbosity = 'Detailed'
if ($Filter) { $cfg.Filter.FullName = $Filter }
$result = Invoke-Pester -Configuration $cfg

$failed = $result.FailedCount + $result.FailedBlocksCount + $result.FailedContainersCount
if (-not $SkipActionlint -and -not $Filter) {
    $actionlint = if ($ActionlintPath) { $ActionlintPath } else { (Get-Command actionlint -ErrorAction Stop).Source }
    if ($ShellcheckDir) { $env:PATH = "$ShellcheckDir$([IO.Path]::PathSeparator)$env:PATH" }
    $hasShellcheck = [bool](Get-Command shellcheck -ErrorAction SilentlyContinue)
    Write-Host "actionlint (shellcheck $(if ($hasShellcheck) { 'on' } else { 'NOT on PATH, run: scripts are not shell-checked' }))"
    $workflows = Get-ChildItem (Join-Path $root '.github' 'workflows') -Filter '*.yml'
    $own = @($workflows | Where-Object Name -ne 'ci.yml' | ForEach-Object FullName)
    & $actionlint @own
    if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "actionlint FAILED on infra workflows" -ForegroundColor Red }
    else { Write-Host "actionlint passed on $($own.Count) infra workflows" -ForegroundColor Green }
    $ci = Join-Path $root '.github' 'workflows' 'ci.yml'
    if (Test-Path $ci) {
        & $actionlint $ci
        if ($LASTEXITCODE -ne 0) { Write-Host "NOTE: actionlint reports problems in ci.yml (not owned by this module, see the report)" -ForegroundColor Yellow }
    }
}
if ($failed -gt 0) { exit 1 }
