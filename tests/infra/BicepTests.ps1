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
            $_.type -in 'Microsoft.Network/virtualNetworks', 'Microsoft.App/managedEnvironments', 'Microsoft.App/containerApps',
                        'Microsoft.App/jobs', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'Microsoft.OperationalInsights/workspaces'
        }
        $created.Count | Should -Be 7
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

    It 'admits the Container Apps subnet through one virtual network rule' {
        $rule = Single 'Microsoft.Sql/servers/virtualNetworkRules' 'the aca subnet'
        $rule.properties.virtualNetworkSubnetId | Should -Be "[variables('acaSubnetId')]"
        $script:Template.variables.acaSubnetName | Should -Be 'aca'
        $script:Template.variables.acaSubnetId | Should -Match "virtualNetworks/subnets', 'lifestream-vnet', variables\('acaSubnetName'\)"
        $rule.properties.ignoreMissingVnetServiceEndpoint | Should -Be $false
    }

    It 'keeps the SQL service endpoint on the aca subnet and no other egress path (no NAT gateway, no public IP)' {
        $vnet = Single 'Microsoft.Network/virtualNetworks' 'the VNet'
        $vnet.properties.addressSpace.addressPrefixes | Should -Contain '10.40.0.0/16'
        $aca = @($vnet.properties.subnets | Where-Object name -eq "[variables('acaSubnetName')]")
        $aca.Count | Should -Be 1
        $aca[0].properties.addressPrefix | Should -Be '10.40.0.0/23'
        @($aca[0].properties.serviceEndpoints.service) | Should -Contain 'Microsoft.Sql'
        OfType 'Microsoft.Network/natGateways' | Should -BeNullOrEmpty
        OfType 'Microsoft.Network/publicIPAddresses' | Should -BeNullOrEmpty
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
    It 'runs in a VNet-integrated environment with only the Consumption profile and no log destination' {
        $env = Single 'Microsoft.App/managedEnvironments' 'the environment'
        $profiles = @($env.properties.workloadProfiles)
        $profiles.Count | Should -Be 1
        $profiles[0].workloadProfileType | Should -Be 'Consumption'
        $env.properties.appLogsConfiguration.destination | Should -Be 'none'
        $env.properties.vnetConfiguration.infrastructureSubnetId | Should -Be "[variables('acaSubnetId')]"
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

    It 'adds nothing that bills at idle: no registry, vault, storage, gateway, private endpoint, Redis or dedicated profile' {
        foreach ($t in 'Microsoft.ContainerRegistry/registries', 'Microsoft.KeyVault/vaults', 'Microsoft.Storage/storageAccounts',
                       'Microsoft.Network/applicationGateways', 'Microsoft.Cache/redis', 'Microsoft.Network/privateEndpoints') {
            OfType $t | Should -BeNullOrEmpty -Because $t
        }
        $script:RawJson | Should -Not -Match '"workloadProfileType": "D\d'
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
