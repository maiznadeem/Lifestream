<#
.SYNOPSIS
  Makes the SQL server firewall hold exactly the Container Apps environment's outbound IP addresses, and nothing else.

.DESCRIPTION
  INFRA-04. The environment is a plain Consumption environment (no virtual network, so no billed public IP or load balancer),
  which means the API reaches Azure SQL from Azure-owned outbound addresses that can change over time. This script reads them,
  then makes the server's firewall rules exactly that set:

    - one rule per address, named aca-outbound-<n> (n = 1, 2, ... in ascending address order), start = end = the address;
    - every other rule is deleted: "Allow anyone" (0.0.0.0-255.255.255.255), "AllowAllWindowsAzureIps" (0.0.0.0-0.0.0.0),
      stale aca-outbound-* rules, hand-added client rules, anything.

  Order is add/update first, delete second, so the API is never left without a matching rule. Running it twice changes nothing.
  It never touches the database, only the server's firewall rules.

  Where the addresses come from: the environment's outboundIpAddresses (az containerapp env show), plus the outboundIpAddresses
  of every container app that runs in that environment (az containerapp list), because Azure publishes them per app on some API
  versions. If both are empty the script FAILS and deletes nothing: an empty answer is a broken query, not an instruction to
  close the server to everyone.

  Security gate: the addresses are shared multi-tenant Azure egress, so the firewall narrows exposure but is not a tenant boundary.
  The script therefore refuses to run (changing nothing, deleting nothing) unless `az sql server ad-only-auth get` returns true.

  Locks: a CanNotDelete lock on the server also blocks deleting its firewall rules (INFRA-08 puts one there). When rules have to be
  deleted and such a lock exists, the script removes the lock, deletes the rules, and puts the identical lock back in a finally block
  (UNLOCK and RELOCK lines in the output), so the server is never left unlocked. Adds and updates happen first and need no lock change.
  Managing locks needs Microsoft.Authorization/locks/write and delete, which the built-in Contributor role does NOT have (only Owner and
  User Access Administrator do). Without that right the lock removal fails, nothing is deleted, the script fails loudly with that
  explanation, and the addresses it needs were already added. A lock at resource-group level, or a ReadOnly lock, is not handled
  (it would also block the adds): the script fails and names it.

  Every az call passes --subscription explicitly. Needs the Azure CLI containerapp extension.

.PARAMETER SubscriptionId  Subscription id (explicit, never the CLI default).
.PARAMETER ResourceGroup   Resource group of the server and the environment.
.PARAMETER Server          Logical SQL server name (without .database.windows.net).
.PARAMETER Environment     Container Apps environment name.
.PARAMETER WhatIfOnly      Prints what would change, changes nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$Environment,
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
$ruleNamePrefix = 'aca-outbound-'

function Invoke-AzJson {
    param([string[]]$Arguments)
    $text = (& az @Arguments --subscription $SubscriptionId --output json --only-show-errors 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments[0..2] -join ' ') failed (exit $LASTEXITCODE): $text" }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $text | ConvertFrom-Json
}

function Invoke-Az {
    param([string[]]$Arguments)
    $text = (& az @Arguments --subscription $SubscriptionId --only-show-errors --output none 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        $hint = if ($text -match 'ScopeLocked|locked') { ' A CanNotDelete lock on the server blocks deleting firewall rules; set enableLocks=false for the run (infra/README.md).' } else { '' }
        throw "az $($Arguments[0..3] -join ' ') failed (exit $LASTEXITCODE): $text$hint"
    }
}

# CanNotDelete locks on the server itself, as objects the script can put back. Anything else that would block the sync stops it.
function Get-ServerLocks {
    $serverId = "/resourceGroups/$ResourceGroup/providers/Microsoft.Sql/servers/$Server"
    $handled = [System.Collections.Generic.List[object]]::new()
    foreach ($lock in @(Invoke-AzJson @('lock', 'list', '--resource-group', $ResourceGroup) | Where-Object { $_ })) {
        $id = [string]$lock.id
        $level = if ($lock.PSObject.Properties['level']) { [string]$lock.level } else { [string]$lock.properties.level }
        $notes = if ($lock.PSObject.Properties['notes']) { [string]$lock.notes } else { [string]$lock.properties.notes }
        $onServer = $id -like "*$serverId/providers/Microsoft.Authorization/locks/*"
        $onGroup = $id -like "*/resourceGroups/$ResourceGroup/providers/Microsoft.Authorization/locks/*"
        if (-not ($onServer -or $onGroup)) { continue }
        if ($onGroup) { throw "FAILED: lock '$($lock.name)' on resource group $ResourceGroup would block the firewall deletes and the script does not manage resource-group locks. Remove it deliberately and rerun." }
        if ($level -ne 'CanNotDelete') { throw "FAILED: lock '$($lock.name)' on $Server has level $level, which also blocks adding rules; the script only manages CanNotDelete locks. Remove it deliberately and rerun." }
        $handled.Add([pscustomobject]@{ Id = $id; Name = [string]$lock.name; Notes = $notes })
    }
    $handled.ToArray()
}

function Get-OutboundIp {
    $found = [System.Collections.Generic.HashSet[string]]::new()
    $env = Invoke-AzJson @('containerapp', 'env', 'show', '--resource-group', $ResourceGroup, '--name', $Environment)
    if ($null -eq $env) { throw "Container Apps environment '$Environment' was not found in resource group '$ResourceGroup'." }
    foreach ($ip in @($env.properties.outboundIpAddresses)) { if ($ip) { [void]$found.Add([string]$ip) } }
    $apps = Invoke-AzJson @('containerapp', 'list', '--resource-group', $ResourceGroup)
    foreach ($app in @($apps)) {
        if (-not $app -or "$($app.properties.environmentId)" -notlike "*/managedEnvironments/$Environment") { continue }
        foreach ($ip in @($app.properties.outboundIpAddresses)) { if ($ip) { [void]$found.Add([string]$ip) } }
    }
    $found
}

# Security gate. The outbound addresses are shared Azure egress (other tenants' containers use them too), so the firewall is not a
# tenant boundary; authentication is. Refuse to open the server to those addresses until Entra-only authentication is on.
function Test-EntraOnly {
    $r = Invoke-AzJson @('sql', 'server', 'ad-only-auth', 'get', '--resource-group', $ResourceGroup, '--name', $Server)
    if ($null -eq $r) { return $false }
    $prop = $r.PSObject.Properties | Where-Object { $_.Name -ieq 'azureAdOnlyAuthentication' } | Select-Object -First 1
    if ($null -eq $prop -and $r.PSObject.Properties.Name -contains 'properties') { $prop = $r.properties.PSObject.Properties | Where-Object { $_.Name -ieq 'azureAdOnlyAuthentication' } | Select-Object -First 1 }
    ($null -ne $prop) -and ($prop.Value -eq $true)
}
if (-not (Test-EntraOnly)) {
    throw "FAILED: Entra-only authentication is not on for $Server (az sql server ad-only-auth get). Refusing to add outbound-address rules: those addresses are shared Azure egress, so SQL authentication must be off first (infra/enable-entra-only.ps1, runbook step 7). Nothing was changed."
}

$addresses = @(Get-OutboundIp)
if ($addresses.Count -eq 0) {
    throw "FAILED: environment '$Environment' returned no outbound IP addresses. Nothing was changed (deleting every firewall rule would lock the API out)."
}
foreach ($ip in $addresses) {
    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsed) -or $parsed.AddressFamily -ne 'InterNetwork' -or $ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
        throw "FAILED: '$ip' is not an IPv4 address. Nothing was changed."
    }
    if ($ip -in '0.0.0.0', '255.255.255.255') { throw "FAILED: the environment reported '$ip', which would open the server to everyone. Nothing was changed." }
}
$sorted = @($addresses | Sort-Object { [version]$_ })
$desired = [ordered]@{}
for ($i = 0; $i -lt $sorted.Count; $i++) { $desired["$ruleNamePrefix$($i + 1)"] = $sorted[$i] }

$existing = @(Invoke-AzJson @('sql', 'server', 'firewall-rule', 'list', '--resource-group', $ResourceGroup, '--server', $Server))
$existing = @($existing | Where-Object { $_ })

$added = 0; $updated = 0; $removed = 0; $kept = 0
foreach ($name in $desired.Keys) {
    $ip = $desired[$name]
    $current = $existing | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if ($null -eq $current) {
        Write-Host "ADD    $name $ip"
        if (-not $WhatIfOnly) { Invoke-Az @('sql', 'server', 'firewall-rule', 'create', '--resource-group', $ResourceGroup, '--server', $Server, '--name', $name, '--start-ip-address', $ip, '--end-ip-address', $ip) }
        $added++
    } elseif ($current.startIpAddress -ne $ip -or $current.endIpAddress -ne $ip) {
        Write-Host "UPDATE $name -> $ip"
        if (-not $WhatIfOnly) { Invoke-Az @('sql', 'server', 'firewall-rule', 'update', '--resource-group', $ResourceGroup, '--server', $Server, '--name', $name, '--start-ip-address', $ip, '--end-ip-address', $ip) }
        $updated++
    } else { $kept++ }
}
$stale = @($existing | Where-Object { -not $desired.Contains([string]$_.name) })
if ($stale.Count -gt 0) {
    $locks = @()
    if (-not $WhatIfOnly) { $locks = @(Get-ServerLocks) }
    $unlocked = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($lock in $locks) {
            Write-Host "UNLOCK $($lock.Name) (CanNotDelete lock on $Server blocks deleting firewall rules; it is restored below)"
            try { Invoke-Az @('lock', 'delete', '--ids', $lock.Id) }
            catch { throw "FAILED: could not remove lock '$($lock.Name)' to delete stale firewall rules: the identity needs Microsoft.Authorization/locks/delete (Owner or User Access Administrator; Contributor lacks it). Nothing was deleted; the addresses the API needs are already in place. Remove the stale rules by hand or with a permitted identity (infra/README.md). Detail: $($_.Exception.Message)" }
            $unlocked.Add($lock)
        }
        foreach ($rule in $stale) {
            Write-Host "DELETE $($rule.name) $($rule.startIpAddress)-$($rule.endIpAddress)"
            if (-not $WhatIfOnly) { Invoke-Az @('sql', 'server', 'firewall-rule', 'delete', '--resource-group', $ResourceGroup, '--server', $Server, '--name', [string]$rule.name, '--yes') }
            $removed++
        }
    } finally {
        $failedRestore = @()
        foreach ($lock in $unlocked) {
            Write-Host "RELOCK $($lock.Name)"
            try { Invoke-Az @('lock', 'create', '--name', $lock.Name, '--lock-type', 'CanNotDelete', '--resource-group', $ResourceGroup, '--resource-name', $Server, '--resource-type', 'Microsoft.Sql/servers', '--resource-provider-namespace', 'Microsoft.Sql', '--notes', $lock.Notes) }
            catch { $failedRestore += "$($lock.Name): $($_.Exception.Message)" }
        }
        if ($failedRestore.Count -gt 0) { Write-Error "FAILED: the CanNotDelete lock(s) could not be put back, the server is UNLOCKED. Recreate with infra/main.bicep (enableLocks=true) now. $($failedRestore -join '; ')" }
    }
}

# Post-check: read the rules back and require exactly the desired set.
if (-not $WhatIfOnly) {
    $after = @(Invoke-AzJson @('sql', 'server', 'firewall-rule', 'list', '--resource-group', $ResourceGroup, '--server', $Server) | Where-Object { $_ })
    $bad = @($after | Where-Object { -not $desired.Contains([string]$_.name) -or $_.startIpAddress -ne $desired[[string]$_.name] -or $_.endIpAddress -ne $desired[[string]$_.name] })
    if ($bad.Count -gt 0 -or $after.Count -ne $desired.Count) { throw "FAILED: after the sync the firewall holds $($after.Count) rule(s), expected exactly $($desired.Count) (aca-outbound-*)." }
}
Write-Host "OK: firewall of $Server holds exactly $($desired.Count) outbound address(es) ($added added, $updated updated, $removed removed, $kept unchanged)."
