<#
.SYNOPSIS
  Deletes all but the newest -Keep versions of a GHCR container package, with guards that make a wrong prune impossible.

.DESCRIPTION
  OWN-03 / INFRA-13. Replaces a third-party prune action so the decision is code under test (tests/infra/ImageVersions.Tests.ps1).
  Guards, each of which aborts BEFORE anything is deleted:
    - -Keep must be at least 1;
    - the package must list at least one version (an empty or failed list is a broken query, not "nothing to keep");
    - the version carrying -DeployedTag must exist;
    - that version must be among the newest -Keep (otherwise the image that is running would be deleted).
  Then every older version is deleted with `gh api -X DELETE`; a failed delete fails the script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Owner,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$DeployedTag,
    [int]$Keep = 3
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'image-versions-lib.ps1')

if ($Keep -lt 1) { throw "FAILED: -Keep must be at least 1 (got $Keep). Nothing was deleted." }
$versions = @(Get-PackageVersion -Owner $Owner -Package $Package)
if ($versions.Count -eq 0) { throw "FAILED: $Package lists no versions. Nothing was deleted." }
$deployed = @($versions | Where-Object { $_.Tags -contains $DeployedTag })
if ($deployed.Count -eq 0) { throw "FAILED: no version of $Package carries the deployed tag $DeployedTag. Nothing was deleted." }
$newest = @($versions | Select-Object -First $Keep)
if (@($newest | Where-Object { $_.Tags -contains $DeployedTag }).Count -eq 0) {
    throw "FAILED: the deployed tag $DeployedTag is not among the newest $Keep versions, so pruning would delete the running image. Nothing was deleted."
}

$old = @($versions | Select-Object -Skip $Keep)
foreach ($v in $old) {
    & gh api -X DELETE "/users/$Owner/packages/container/$Package/versions/$($v.Id)" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FAILED: deleting version $($v.Id) of $Package failed (exit $LASTEXITCODE)." }
    Write-Host "DELETED version $($v.Id) ($($v.Tags -join ','))"
}
Write-Host "OK: kept the newest $(@($newest).Count) version(s) of $Package, deleted $($old.Count)."
