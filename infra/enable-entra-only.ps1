<#
.SYNOPSIS
  Turns Microsoft Entra-only authentication on for the SQL server, then proves it is on.

.DESCRIPTION
  INFRA-05. After this, SQL logins (including the old admin login) cannot sign in. It is the LAST step of the runbook: run it only
  after the API's managed identity login and the migrate job have been proven against the database (infra/README.md step 9), which
  is why -ManagedIdentityLoginProven is mandatory. The same switch exists in main.bicep (parameter entraOnlyAuthentication);
  this script is the imperative form with a post-check, because a template re-apply reports success without reading the result.

  Steps: require an Entra administrator on the server (enabling Entra-only without one locks everybody out); if Entra-only is
  already on, change nothing (idempotent); otherwise enable it; finally read `az sql server ad-only-auth get` and fail unless it
  reports true. Every az call passes --subscription explicitly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$Server,
    [switch]$ManagedIdentityLoginProven
)

$ErrorActionPreference = 'Stop'
if (-not $ManagedIdentityLoginProven) { throw 'Refusing to run: pass -ManagedIdentityLoginProven only after the API and migrate job have signed in with their managed identities.' }

function Invoke-AzJson {
    param([string[]]$Arguments)
    $text = (& az @Arguments --subscription $SubscriptionId --output json --only-show-errors 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments[0..3] -join ' ') failed (exit $LASTEXITCODE): $text" }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $text | ConvertFrom-Json
}

# The CLI prints azureAdOnlyAuthentication; the ARM name is azureADOnlyAuthentication. Accept either, case-insensitively.
function Get-EntraOnly {
    $r = Invoke-AzJson @('sql', 'server', 'ad-only-auth', 'get', '--resource-group', $ResourceGroup, '--name', $Server)
    if ($null -eq $r) { return $false }
    $prop = $r.PSObject.Properties | Where-Object { $_.Name -ieq 'azureAdOnlyAuthentication' } | Select-Object -First 1
    if ($null -eq $prop) { $prop = $r.properties.PSObject.Properties | Where-Object { $_.Name -ieq 'azureAdOnlyAuthentication' } | Select-Object -First 1 }
    ($null -ne $prop) -and ($prop.Value -eq $true)
}

$admins = @(Invoke-AzJson @('sql', 'server', 'ad-admin', 'list', '--resource-group', $ResourceGroup, '--server-name', $Server) | Where-Object { $_ })
if ($admins.Count -eq 0) { throw "FAILED: $Server has no Microsoft Entra administrator. Entra-only authentication would lock everyone out. Nothing was changed." }

if (Get-EntraOnly) {
    Write-Host "OK: Entra-only authentication is already on for $Server (nothing changed)."
    return
}

Invoke-AzJson @('sql', 'server', 'ad-only-auth', 'enable', '--resource-group', $ResourceGroup, '--name', $Server) | Out-Null
if (-not (Get-EntraOnly)) { throw "FAILED: after enabling, 'az sql server ad-only-auth get' for $Server does not report true." }
Write-Host "OK: Entra-only authentication is on for $Server (verified with az sql server ad-only-auth get)."
