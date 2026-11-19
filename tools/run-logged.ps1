<#
.SYNOPSIS
  Runs a command with its output written to a file, scans that file for credentials, and only then prints it to the job log.

.DESCRIPTION
  A CI job log is public the moment it prints. Build and test output can echo a connection string (a failing test, a verbose
  provider), so it never goes to the log live: the output is written to -Log, scanned with tools/scan-artifacts.ps1 (both scanners),
  and printed only after a clean scan. When the scan finds something, only a redacted summary is printed (file, rule, line: never a
  value) and the script exits 1, whatever the command returned. Otherwise the exit code is the command's own.

    pwsh tools/run-logged.ps1 -Log "$RUNNER_TEMP/test-logs/build.log" -Command 'dotnet build Lifestream.slnx -c Release'

.PARAMETER Log      The file the output is written to (its folder is created); keep it where a later step scans and uploads it.
.PARAMETER Command  The command line to run, as one string (a PowerShell expression: the command and its arguments).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Log,
    [Parameter(Mandatory)][string]$Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Command.Trim()) { Write-Host 'ERROR: no command given'; exit 2 }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($Log))) | Out-Null
$ErrorActionPreference = 'Continue'
$exe = ($Command.Trim() -split '\s+')[0]
$global:LASTEXITCODE = 0
& ([scriptblock]::Create($Command)) 2>&1 | Out-File -LiteralPath $Log -Encoding utf8
$code = $LASTEXITCODE
if ($null -eq $code) { $code = 0 }
$ErrorActionPreference = 'Stop'

$report = "$Log.scan.json"
$scanOut = & (Join-Path $PSScriptRoot 'scan-artifacts.ps1') -Path $Log -ReportPath $report *>&1 | Out-String
$scan = $LASTEXITCODE
if ($scan -eq 0) {
    Get-Content -LiteralPath $Log | Write-Host
    Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
    exit $code
}
Write-Host "::error::the output of '$exe' was NOT printed: the credential scan of it exited $scan (1 = a credential found, 2 = the scan could not run)."
Write-Host "Redacted summary (file, rule, line; never a value). The full output is in the file $Log on the runner."
($scanOut -split "`n" | Where-Object { $_ -match '^(FINDING|ERROR|scanned)' }) -join "`n" | Write-Host
exit 1
