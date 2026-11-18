<#
.SYNOPSIS
  After a prune: requires at most -Max versions, the deployed tag among them, and `docker manifest inspect` to succeed on the image.

.DESCRIPTION
  OWN-03 / INFRA-13. The checks are code under test (tests/infra/ImageVersions.Tests.ps1), not shell text in a workflow.
    - zero versions listed is a failure (broken query), not a pass;
    - more than -Max versions is a failure;
    - the deployed tag must be on one of the remaining versions;
    - `docker manifest inspect <Image>:<DeployedTag>` must exit 0.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Owner,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$DeployedTag,
    [int]$Max = 3
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'image-versions-lib.ps1')

$versions = @(Get-PackageVersion -Owner $Owner -Package $Package)
Write-Host "Remaining package versions (id, tags):"
foreach ($v in $versions) { Write-Host "  $($v.Id)  $($v.Tags -join ',')" }
if ($versions.Count -eq 0) { throw "FAILED: $Package lists no versions." }
if ($versions.Count -gt $Max) { throw "FAILED: $($versions.Count) versions remain, expected at most $Max." }
if (@($versions | Where-Object { $_.Tags -contains $DeployedTag }).Count -eq 0) { throw "FAILED: the deployed tag $DeployedTag is not among the remaining versions." }

& docker manifest inspect "${Image}:$DeployedTag" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "FAILED: docker manifest inspect ${Image}:$DeployedTag exited $LASTEXITCODE." }
Write-Host "OK: $($versions.Count) version(s) remain and ${Image}:$DeployedTag passes docker manifest inspect."
