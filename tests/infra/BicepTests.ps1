# Assertions over infra/main.bicep, run against the ARM JSON that `bicep build` produces (nothing is deployed).
# Run: pwsh tests/infra/run.ps1   (Pester 5 or later)

BeforeAll {
    $script:InfraDir = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'infra')).Path
    $bicepFile = Join-Path $script:InfraDir 'main.bicep'
    if (-not (Test-Path $bicepFile)) { throw "infra/main.bicep does not exist" }

    $json = & az bicep build --file $bicepFile --stdout 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { throw "az bicep build failed for $bicepFile" }
    $script:RawJson = ($json -join "`n")
    $script:Template = $script:RawJson | ConvertFrom-Json -Depth 100
    $script:Resources = @($script:Template.resources)

    function script:OfType([string]$type) { @($script:Resources | Where-Object { $_.type -eq $type }) }
    function script:Single([string]$type, [string]$because) {
        $found = OfType $type
        if ($found.Count -ne 1) { throw "expected exactly one $type ($because) but found $($found.Count)" }
        $found[0]
    }
}

Describe 'template shape' {
    It 'builds and declares the required outputs (apiUrl, apiPrincipalId, migratorPrincipalId)' {
        $names = $script:Template.outputs.PSObject.Properties.Name
        $names | Should -Contain 'apiUrl'
        $names | Should -Contain 'apiPrincipalId'
        $names | Should -Contain 'migratorPrincipalId'
    }

    It 'deploys at resource-group scope' {
        $script:Template.'$schema' | Should -Match '/deploymentTemplate\.json#'
    }

    It 'names every resource it creates with the lifestream- prefix' {
        $created = $script:Resources | Where-Object {
            $_.type -in 'Microsoft.App/managedEnvironments', 'Microsoft.App/containerApps',
                        'Microsoft.App/jobs', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'Microsoft.OperationalInsights/workspaces'
        }
        $created.Count | Should -Be 6
        foreach ($r in $created) { $r.name | Should -Match '^lifestream-' }
    }
}

Describe 'INFRA-04 SQL firewall: no allow-all, only the API environment' {
    It 'declares no firewall rule at all (the legacy rules are removed by the runbook, nothing recreates them)' {
        OfType 'Microsoft.Sql/servers/firewallRules' | Should -BeNullOrEmpty
    }

    It 'never mentions 0.0.0.0, 255.255.255.255, AllowAllWindowsAzureIps or "Allow anyone" anywhere in the template' {
        $script:RawJson | Should -Not -Match '255\.255\.255\.255'
        $script:RawJson | Should -Not -Match '(?<![\d.])0\.0\.0\.0(?![\d.])'
        $script:RawJson | Should -Not -Match 'AllowAllWindowsAzureIps'
        $script:RawJson | Should -Not -Match 'Allow anyone'
    }

    It 'has no virtual network, subnet or virtual network rule, and no network resource of any kind (OWN-02: a VNet environment bills a public IP and a load balancer at idle)' {
        OfType 'Microsoft.Network/virtualNetworks' | Should -BeNullOrEmpty
        OfType 'Microsoft.Sql/servers/virtualNetworkRules' | Should -BeNullOrEmpty
        @($script:Resources | Where-Object { $_.type -like 'Microsoft.Network/*' }) | Should -BeNullOrEmpty
        $script:RawJson | Should -Not -Match 'vnetConfiguration|infrastructureSubnetId|serviceEndpoints|acaSubnet'
    }

    It 'leaves the firewall to infra/sync-sql-firewall.ps1: the script, the deploy step and the weekly workflow exist' {
        $root = Split-Path $script:InfraDir -Parent
        Test-Path (Join-Path $script:InfraDir 'sync-sql-firewall.ps1') | Should -BeTrue
        Test-Path (Join-Path $root '.github' 'workflows' 'sync-sql-firewall.yml') | Should -BeTrue
        (Get-Content (Join-Path $root '.github' 'workflows' 'deploy-api.yml') -Raw) | Should -Match 'infra/sync-sql-firewall\.ps1'
    }
}

Describe 'INFRA-05 Entra-only authentication' {
    It 'sets an Entra administrator and drives Entra-only from one parameter that defaults to false' {
        $admin = Single 'Microsoft.Sql/servers/administrators' 'the Entra admin'
        $admin.properties.administratorType | Should -Be 'ActiveDirectory'
        $only = Single 'Microsoft.Sql/servers/azureADOnlyAuthentications' 'the Entra-only switch'
        $only.name | Should -Match 'Default'
        $only.properties.azureADOnlyAuthentication | Should -Be "[parameters('entraOnlyAuthentication')]"
        $script:Template.parameters.entraOnlyAuthentication.type | Should -Be 'bool'
        $script:Template.parameters.entraOnlyAuthentication.defaultValue | Should -Be $false
    }

    It 'ships infra/enable-entra-only.ps1 (idempotent, post-checked; behaviour in EntraOnly.Tests.ps1) and the runbook orders it after the managed identity login is proven' {
        Test-Path (Join-Path $script:InfraDir 'enable-entra-only.ps1') | Should -BeTrue
        $readme = Get-Content (Join-Path $script:InfraDir 'README.md') -Raw
        $readme | Should -Match 'enable-entra-only\.ps1'
        $readme | Should -Match '-ManagedIdentityLoginProven'
    }

    It 'lets the final apply flip the switch from the parameter file (LIFESTREAM_ENTRA_ONLY=true)' {
        $paramFile = Get-Content (Join-Path $script:InfraDir 'main.bicepparam') -Raw
        $paramFile | Should -Match 'param entraOnlyAuthentication = .*LIFESTREAM_ENTRA_ONLY'
    }
}

Describe 'INFRA-06 identities: managed identity for the API, a passwordless migrator' {
    It 'gives the API a system-assigned identity and the migrate job a user-assigned identity named lifestream-migrator' {
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $api.identity.type | Should -Be 'SystemAssigned'
        $migrator = @(OfType 'Microsoft.ManagedIdentity/userAssignedIdentities' | Where-Object { $_.name -eq 'lifestream-migrator' })
        $migrator.Count | Should -Be 1
        $job = Single 'Microsoft.App/jobs' 'the migrate job'
        $job.identity.type | Should -Be 'UserAssigned'
    }

    It 'carries no SQL password, SQL authentication or key anywhere: connection strings use the managed identity only' {
        $script:RawJson | Should -Not -Match '(?i)password\s*='
        $script:RawJson | Should -Not -Match '(?i)\bpwd\s*='
        $script:RawJson | Should -Not -Match '(?i)authentication=sql'
        $script:Template.variables.sqlConnectionBase | Should -Match 'Authentication=Active Directory Managed Identity'
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $cs = @($api.properties.template.containers[0].env | Where-Object name -eq 'ConnectionStrings__Lifestream')
        $cs.Count | Should -Be 1
        $cs[0].value | Should -Be "[variables('sqlConnectionBase')]"
    }

    It 'takes every secret value from a secure parameter, never a literal' {
        foreach ($name in 'jwtSigningKey', 'ownerAdminPassword') {
            $script:Template.parameters.$name.type | Should -Be 'securestring'
            $script:Template.parameters.$name.PSObject.Properties.Name | Should -Not -Contain 'defaultValue'
        }
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $secret = @($api.properties.configuration.secrets | Where-Object name -eq 'jwt-signing-key')
        $secret.Count | Should -Be 1
        $secret[0].value | Should -Be "[parameters('jwtSigningKey')]"
        $job = Single 'Microsoft.App/jobs' 'the migrate job'
        $jobSecret = @($job.properties.configuration.secrets | Where-Object name -eq 'owner-admin-password')
        $jobSecret.Count | Should -Be 1
        $jobSecret[0].value | Should -Be "[parameters('ownerAdminPassword')]"
    }

    It 'configures the migrate job as a manual-trigger job (nothing wakes the database on a schedule)' {
        $job = Single 'Microsoft.App/jobs' 'the migrate job'
        $job.name | Should -Be 'lifestream-migrate'
        $job.properties.configuration.triggerType | Should -Be 'Manual'
        $job.properties.configuration.PSObject.Properties.Name | Should -Not -Contain 'scheduleTriggerConfig'
    }

    It 'signs the migrate job in as the migrator identity, by client id' {
        $job = Single 'Microsoft.App/jobs' 'the migrate job'
        $cs = @($job.properties.template.containers[0].env | Where-Object name -eq 'ConnectionStrings__Lifestream')
        $cs[0].value | Should -Match "reference\(resourceId\('Microsoft.ManagedIdentity/userAssignedIdentities', 'lifestream-migrator'\).*\.clientId"
    }
}

Describe 'INFRA-07 auditing' {
    It 'turns SQL server auditing on and sends it to Azure Monitor (no storage account, so no idle cost)' {
        $audit = Single 'Microsoft.Sql/servers/auditingSettings' 'server auditing'
        $audit.properties.state | Should -Be 'Enabled'
        $audit.properties.isAzureMonitorTargetEnabled | Should -Be $true
        $audit.properties.PSObject.Properties.Name | Should -Not -Contain 'storageEndpoint'
        OfType 'Microsoft.Storage/storageAccounts' | Should -BeNullOrEmpty
    }

    It 'routes the master database SQLSecurityAuditEvents to a Log Analytics workspace with a daily cap and 31 days or less retention' {
        $diag = Single 'Microsoft.Insights/diagnosticSettings' 'the audit diagnostic setting'
        $diag.scope | Should -Match "databases', parameters\('sqlServerName'\), 'master'"
        @($diag.properties.logs | Where-Object { $_.category -eq 'SQLSecurityAuditEvents' -and $_.enabled }).Count | Should -Be 1
        $ws = Single 'Microsoft.OperationalInsights/workspaces' 'the audit workspace'
        "$($ws.properties.workspaceCapping.dailyQuotaGb)" | Should -Match '0\.1'
        $script:Template.parameters.auditRetentionDays.maxValue | Should -BeLessOrEqual 31
    }

    It 'does not enable Defender for SQL (paid)' {
        OfType 'Microsoft.Sql/servers/advancedThreatProtectionSettings' | Should -BeNullOrEmpty
        OfType 'Microsoft.Sql/servers/securityAlertPolicies' | Should -BeNullOrEmpty
        $script:RawJson | Should -Not -Match 'Microsoft\.Security/'
    }
}

Describe 'INFRA-08 locks and backups' {
    It 'puts a CanNotDelete lock on the SQL server and on the database (on by default)' {
        $locks = OfType 'Microsoft.Authorization/locks'
        $locks.Count | Should -Be 2
        foreach ($l in $locks) {
            $l.properties.level | Should -Be 'CanNotDelete'
            $l.condition | Should -Be "[parameters('enableLocks')]"
        }
        $script:Template.parameters.enableLocks.defaultValue | Should -Be $true
        @($locks | Where-Object { $_.scope -eq "[resourceId('Microsoft.Sql/servers', parameters('sqlServerName'))]" }).Count | Should -Be 1
        @($locks | Where-Object { $_.scope -eq "[resourceId('Microsoft.Sql/servers/databases', parameters('sqlServerName'), parameters('sqlDatabaseName'))]" }).Count | Should -Be 1
    }

    It 'sets short-term backup retention from a parameter and documents that 7 days is the free-offer ceiling against the 14 asked for' {
        $policy = Single 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies' 'PITR'
        $policy.properties.retentionDays | Should -Be "[parameters('backupRetentionDays')]"
        $script:Template.parameters.backupRetentionDays.type | Should -Be 'int'
        $script:Template.parameters.backupRetentionDays.defaultValue | Should -Be 7
        $script:Template.parameters.backupRetentionDays.metadata.description | Should -Match '14'
        $script:Template.parameters.backupRetentionDays.metadata.description | Should -Match '7 days'
    }
}

Describe 'INFRA-09 / OWN-02 container app on Consumption, scale to zero' {
    It 'runs in a plain environment (no VNet) with only the Consumption profile and no log destination' {
        $env = Single 'Microsoft.App/managedEnvironments' 'the environment'
        $profiles = @($env.properties.workloadProfiles)
        $profiles.Count | Should -Be 1
        $profiles[0].workloadProfileType | Should -Be 'Consumption'
        $env.properties.appLogsConfiguration.destination | Should -Be 'none'
        $env.properties.PSObject.Properties.Name | Should -Not -Contain 'vnetConfiguration'
        $env.properties.zoneRedundant | Should -Be $false
    }

    It 'scales from 0 to at most 1 replica (the ledger allows up to 2) at 0.25 vCPU / 0.5 GiB' {
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $api.properties.template.scale.minReplicas | Should -Be 0
        $api.properties.template.scale.maxReplicas | Should -Be 1
        $api.properties.template.scale.maxReplicas | Should -BeLessOrEqual 2
        $c = $api.properties.template.containers[0]
        ([string]$c.resources.cpu -replace "^\[json\('|'\)\]$", '') | Should -Be '0.25'
        $c.resources.memory | Should -Be '0.5Gi'
        $api.properties.workloadProfileName | Should -Be 'Consumption'
    }

    It 'has a startup probe on /healthz' {
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $probes = @($api.properties.template.containers[0].probes | Where-Object type -eq 'Startup')
        $probes.Count | Should -Be 1
        $probes[0].httpGet.path | Should -Be '/healthz'
        $probes[0].httpGet.port | Should -Be 8080
    }

    It 'exposes external HTTPS only on port 8080' {
        $api = Single 'Microsoft.App/containerApps' 'the API'
        $ing = $api.properties.configuration.ingress
        $ing.external | Should -Be $true
        $ing.allowInsecure | Should -Be $false
        $ing.targetPort | Should -Be 8080
    }

    It 'configures a monthly budget of 1 with an alert at 50 percent to the owner' {
        $b = Single 'Microsoft.Consumption/budgets' 'the budget'
        $script:Template.parameters.budgetAmount.defaultValue | Should -Be 1
        $b.properties.amount | Should -Be "[parameters('budgetAmount')]"
        $b.properties.timeGrain | Should -Be 'Monthly'
        $n = @($b.properties.notifications.PSObject.Properties.Value | Where-Object { $_.threshold -eq 50 })
        $n.Count | Should -Be 1
        $n[0].enabled | Should -Be $true
        $n[0].thresholdType | Should -Be 'Actual'
        $n[0].contactEmails | Should -Contain "[parameters('ownerEmail')]"
    }

    It 'gives the budget a fixed start date on the first of a month, so re-applying the template in a later month does not change it' {
        $p = $script:Template.parameters.budgetStartDate
        $p.defaultValue | Should -Match '^\d{4}-\d{2}-01$'
        $script:RawJson | Should -Not -Match 'utcNow'
        $b = Single 'Microsoft.Consumption/budgets' 'the budget'
        $b.properties.timePeriod.startDate | Should -Be "[parameters('budgetStartDate')]"
    }

    It 'adds nothing that bills at idle: no registry, vault, storage, gateway, private endpoint, Redis or dedicated profile' {
        foreach ($t in 'Microsoft.ContainerRegistry/registries', 'Microsoft.KeyVault/vaults', 'Microsoft.Storage/storageAccounts',
                       'Microsoft.Network/applicationGateways', 'Microsoft.Cache/redis', 'Microsoft.Network/privateEndpoints') {
            OfType $t | Should -BeNullOrEmpty -Because $t
        }
        $script:RawJson | Should -Not -Match '"workloadProfileType": "D\d'
    }
}

# OWN-02 allow-list. Every resource type the compiled template may contain, each with the reason it costs nothing while there is
# no traffic. A type that is not listed fails the build, so a new billable resource has to be reviewed and added here on purpose.
# Sources (Microsoft Learn, read 2026-11-19): container-apps/billing (consumption plan bills vCPU-s, GiB-s and requests only; zero
# replicas = no charge; jobs bill only while an execution runs; no plan-management fee without a Dedicated profile or private
# endpoint), container-apps/custom-virtual-networks#managed-resources (the public IP and load balancer exist only with a custom VNet),
# azure-monitor/logs/log-analytics-workspace-overview#cost (no cost for creating a workspace; charged for ingestion and retention),
# entra managed-identities overview ("free with Microsoft Entra ID"), cost-management budgets tutorial (alerts only).
Describe 'OWN-02 allow-list of resource types that cost nothing while idle' {
    BeforeAll {
        $script:IdleFree = [ordered]@{
            'Microsoft.App/managedEnvironments'                                             = 'plain Consumption environment: no VNet, so no public IP or load balancer; logs destination none'
            'Microsoft.App/containerApps'                                                   = 'Consumption app, minReplicas 0: no resource charge at zero replicas; first 2M requests a month free'
            'Microsoft.App/jobs'                                                            = 'manual job: bills only while an execution runs'
            'Microsoft.ManagedIdentity/userAssignedIdentities'                              = 'managed identities are free'
            'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials' = 'no charge'
            'Microsoft.Authorization/roleAssignments'                                       = 'no charge'
            'Microsoft.Authorization/locks'                                                 = 'no charge'
            'Microsoft.Sql/servers'                                                         = 'logical server: no charge on its own'
            'Microsoft.Sql/servers/administrators'                                          = 'no charge'
            'Microsoft.Sql/servers/azureADOnlyAuthentications'                              = 'no charge'
            'Microsoft.Sql/servers/auditingSettings'                                        = 'Azure Monitor target only (no storage account); ingestion capped, see workspace'
            'Microsoft.Sql/servers/databases'                                               = 'free offer (useFreeLimit) with AutoPause on exhaustion: asserted in the INFRA-10 test'
            'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies'              = 'included with the database'
            'Microsoft.OperationalInsights/workspaces'                                      = 'no cost to hold; billed per GB ingested, capped at 0.1 GB a day (3 GB a month, inside the 5 GB free grant), retention 30-31 days included'
            'Microsoft.Insights/diagnosticSettings'                                         = 'no charge itself; the data it sends is the capped ingestion above'
            'Microsoft.Consumption/budgets'                                                 = 'alerts only'
        }
    }

    It 'contains only resource types on the reviewed list' {
        $actual = @($script:Resources | ForEach-Object { $_.type } | Sort-Object -Unique)
        $unlisted = @($actual | Where-Object { -not $script:IdleFree.Contains($_) })
        $unlisted | Should -BeNullOrEmpty -Because "these types are not reviewed as free at idle: $($unlisted -join ', ')"
    }

    It 'has no reviewed type that the template no longer uses (keep the list honest)' {
        $actual = @($script:Resources | ForEach-Object { $_.type } | Sort-Object -Unique)
        @($script:IdleFree.Keys | Where-Object { $_ -notin $actual }) | Should -BeNullOrEmpty
    }

    It 'caps the audit workspace ingestion so the free grant cannot be exceeded' {
        $ws = @($script:Resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces')[0]
        [double]("$($ws.properties.workspaceCapping.dailyQuotaGb)" -replace '[^0-9.]', '') | Should -BeLessOrEqual 0.1
        $ws.properties.sku.name | Should -Be 'PerGB2018'
        $ws.properties.retentionInDays | Should -BeLessOrEqual 31
    }

    It 'keeps the environment without a VNet and the app at zero minimum replicas' {
        $env = @($script:Resources | Where-Object type -eq 'Microsoft.App/managedEnvironments')[0]
        $env.properties.PSObject.Properties.Name | Should -Not -Contain 'vnetConfiguration'
        $env.properties.appLogsConfiguration.destination | Should -Be 'none'
        @($env.properties.workloadProfiles | Where-Object workloadProfileType -ne 'Consumption') | Should -BeNullOrEmpty
        @($script:Resources | Where-Object type -eq 'Microsoft.App/containerApps')[0].properties.template.scale.minReplicas | Should -Be 0
    }
}

Describe 'INFRA-10 free-offer serverless database' {
    It 'declares BloodBank as GP_S_Gen5 with the free limit and AutoPause on exhaustion' {
        $db = Single 'Microsoft.Sql/servers/databases' 'BloodBank'
        $db.properties.useFreeLimit | Should -Be $true
        $db.properties.freeLimitExhaustionBehavior | Should -Be 'AutoPause'
        $db.properties.autoPauseDelay | Should -Be 60
        $db.sku.name | Should -Be 'GP_S_Gen5'
        $db.sku.tier | Should -Be 'GeneralPurpose'
        $db.properties.requestedBackupStorageRedundancy | Should -Be 'Local'
        $script:Template.parameters.sqlDatabaseName.defaultValue | Should -Be 'BloodBank'
    }
}

Describe 'INFRA-13 keyless deployment identity' {
    It 'creates a deployer identity trusted only for the GitHub repository and branch, through a federated credential and no secret' {
        $cred = Single 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials' 'GitHub OIDC'
        $cred.properties.issuer | Should -Be 'https://token.actions.githubusercontent.com'
        $cred.properties.audiences | Should -Contain 'api://AzureADTokenExchange'
        $cred.properties.subject | Should -Be "[format('repo:{0}:ref:refs/heads/{1}', parameters('githubRepository'), parameters('githubBranch'))]"
        $script:Template.parameters.githubRepository.defaultValue | Should -Be 'MaizNadeem/Lifestream-WPF'
        $script:Template.parameters.githubBranch.defaultValue | Should -Be 'master'
        $script:Template.outputs.PSObject.Properties.Name | Should -Contain 'deployerClientId'
    }

    It 'gives the deployer Contributor on the resource group only (no explicit wider scope, no Owner)' {
        $ra = Single 'Microsoft.Authorization/roleAssignments' 'deployer role'
        $ra.PSObject.Properties.Name | Should -Not -Contain 'scope'
        $script:Template.variables.contributorRoleId | Should -Be 'b24988ac-6180-42a0-ab88-20f7382dd24c'
        $ra.properties.roleDefinitionId | Should -Match "variables\('contributorRoleId'\)"
        $ra.properties.principalType | Should -Be 'ServicePrincipal'
    }

    It 'stores no client secret for the deployer anywhere in the template' {
        $script:RawJson | Should -Not -Match '(?i)client_?secret'
        $script:RawJson | Should -Not -Match '(?i)passwordCredentials'
    }
}

Describe 'parameter file' {
    It 'reads every secret and personal value from environment variables and holds no literal credential' {
        $text = Get-Content (Join-Path $script:InfraDir 'main.bicepparam') -Raw
        $text | Should -Match "using '\./main\.bicep'"
        $text | Should -Match "param jwtSigningKey = readEnvironmentVariable\('LIFESTREAM_JWT_SIGNING_KEY'\)"
        $text | Should -Match "param ownerAdminPassword = readEnvironmentVariable\('LIFESTREAM_OWNER_ADMIN_PASSWORD'\)"
        $text | Should -Not -Match "(?im)^\s*param\s+\w*(password|key|secret)\w*\s*=\s*'"
    }

    It 'compiles to parameters with the secrets filled from the environment, Entra-only off and locks on by default' {
        $paramFile = Join-Path $script:InfraDir 'main.bicepparam'
        $vars = @{
            LIFESTREAM_API_IMAGE = 'ghcr.io/example/image:test'; LIFESTREAM_ENTRA_ADMIN_LOGIN = 'admin-group'
            LIFESTREAM_ENTRA_ADMIN_OBJECT_ID = '00000000-0000-0000-0000-000000000001'; LIFESTREAM_OWNER_EMAIL = 'owner@example.invalid'
            LIFESTREAM_JWT_SIGNING_KEY = 'placeholder-not-a-secret'; LIFESTREAM_OWNER_ADMIN_PASSWORD = 'placeholder-not-a-secret'
        }
        $old = @{}
        foreach ($k in $vars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
        try {
            $out = (& az bicep build-params --file $paramFile --stdout 2>$null) -join "`n"
            $LASTEXITCODE | Should -Be 0
        } finally { foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
        $p = ($out | ConvertFrom-Json -Depth 50).parametersJson | ConvertFrom-Json -Depth 50
        $p.parameters.entraOnlyAuthentication.value | Should -Be $false
        $p.parameters.enableLocks.value | Should -Be $true
        $p.parameters.apiImage.value | Should -Be 'ghcr.io/example/image:test'
    }
}
