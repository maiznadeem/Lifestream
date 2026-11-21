// Lifestream 2.0 infrastructure. Resource-group scope: deploy into resource group "Lifestream".
//
// Costs nothing while idle: a plain Container Apps Consumption environment (no custom virtual network, because a VNet
// environment bills a public IP and a load balancer around the clock), minimum replicas 0, no registry (images come from GHCR),
// no Key Vault, no gateway, no private endpoint, no container log destination. The SQL server and the BloodBank database
// already exist (serverless free offer); they are declared here so the Entra-only switch, audit, locks and backup settings
// are code. The SQL firewall is NOT declared here: infra/sync-sql-firewall.ps1, run by hand, makes it equal to the environment's
// outbound addresses (a template cannot delete rules it does not declare). tests/infra/BicepTests.ps1 holds an allow-list of resource
// types that cost nothing at idle; a type outside it fails the build. Nothing in this file is applied by the infra builder;
// see infra/README.md for the order.

targetScope = 'resourceGroup'

// ---------------------------------------------------------------- parameters

@description('Region of the Container Apps environment and the SQL server (currently eastus). The API runs next to the database to keep latency low; the resource group\'s own region is not used.')
param location string = 'eastus'

@description('Existing logical SQL server.')
param sqlServerName string = 'lifestream-server'

@description('Existing serverless free-offer database.')
param sqlDatabaseName string = 'BloodBank'

@description('Full image reference of the API, for example ghcr.io/maiznadeem/lifestream-api:<sha>. The deploy workflow moves the running app to newer images; pass the currently running image when re-applying this template, or the app goes back to this one.')
param apiImage string

@description('Display name (user principal name) or group name of the Microsoft Entra administrator of the SQL server. The identity choice is the owner\'s.')
param entraAdminLogin string

@description('Object id of that Entra user or group.')
param entraAdminObjectId string

@description('Tenant of the Entra administrator.')
param entraTenantId string = tenant().tenantId

@description('Turns SQL authentication off entirely (the old admin login stops working). Defaults to TRUE so that re-applying this template can never switch SQL logins back on. Only the one-time bootstrap (infra/README.md step 3) passes false explicitly, until the Entra administrator, the contained database users and the API have been proven.')
param entraOnlyAuthentication bool = true

@description('CanNotDelete locks on the SQL server and database. Locks apply to child resources too, so delete the two legacy firewall rules first (infra/README.md step 4) and set this to false for any run that must delete something.')
param enableLocks bool = true

@minValue(1)
@maxValue(35)
@description('Point-in-time restore window in days. The free offer with auto-pause on exhaustion caps it at 7 days (Microsoft Learn, Azure SQL Database free tier FAQ), which is what Azure reports today; requirement INFRA-08 asks for 14 or more, so raise this only if the offer ever allows it, because Azure rejects a value above what the offer allows.')
param backupRetentionDays int = 7

@description('Email address that receives the budget alerts.')
param ownerEmail string

@description('First day of the month the budget starts in (yyyy-MM-01). A fixed value on purpose: a default computed at deployment time would change the start date of the existing budget on every re-apply in a later month, which Azure rejects. Never change it after the first apply.')
param budgetStartDate string = '2026-11-01'

@description('Monthly budget in the billing currency. The design budget is 1.')
param budgetAmount int = 1

@secure()
@description('HMAC key that signs access tokens. Stored as the Container Apps secret jwt-signing-key; never committed.')
param jwtSigningKey string

@secure()
@description('Initial password of the owner admin account that the migrate job creates. Stored as the job secret owner-admin-password; never committed.')
param ownerAdminPassword string

@secure()
@description('Shared secret the Cloudflare Worker lifestream-api-edge (edge/api) sends as X-Origin-Auth. Stored as the Container Apps secret origin-auth and as the Worker secret ORIGIN_AUTH; never committed.')
param originAuthSecret string

@description('log: requests without the Worker secret are served and logged (rollout). enforce: they get 403. /healthz is always served (the startup probe).')
@allowed([ 'log', 'enforce' ])
param originAuthMode string = 'enforce'

@description('The only addresses the API ingress accepts: the IPv4 ranges of Cloudflare (https://api.cloudflare.com/client/v4/ips, 2026-11-22). The Worker reaches the container from them; the container address has no IPv6. Cloudflare announces changes to the list well ahead; update it here and re-apply. An empty list opens the ingress to everyone.')
param ingressAllowedRanges array = [
  '173.245.48.0/20'
  '103.21.244.0/22'
  '103.22.200.0/22'
  '103.31.4.0/22'
  '141.101.64.0/18'
  '108.162.192.0/18'
  '190.93.240.0/20'
  '188.114.96.0/20'
  '197.234.240.0/22'
  '198.41.128.0/17'
  '162.158.0.0/15'
  '104.16.0.0/13'
  '104.24.0.0/14'
  '172.64.0.0/13'
  '131.0.72.0/22'
]

@description('GitHub repository allowed to deploy through OIDC (owner/name).')
param githubRepository string = 'maiznadeem/Lifestream'

@description('Branch whose workflow runs may deploy.')
param githubBranch string = 'master'

@description('Days the audit events are kept in the Log Analytics workspace (31 or fewer are free).')
@minValue(30)
@maxValue(31)
param auditRetentionDays int = 30

// ---------------------------------------------------------------- names and constants

var sqlFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

// ---------------------------------------------------------------- identities

resource migrator 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lifestream-migrator'
  location: location
}

// The identity GitHub Actions signs in as. Trust is a federated credential bound to one repository and branch, so
// GitHub stores no cloud secret (INFRA-13).
resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'lifestream-deployer'
  location: location
}

resource deployerFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-${githubBranch}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:ref:refs/heads/${githubBranch}'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

resource deployerContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployer.id, contributorRoleId)
  properties: {
    principalId: deployer.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
  }
}

// ---------------------------------------------------------------- SQL server, database, access

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled' // reached over the public endpoint; firewall rules are set by infra/sync-sql-firewall.ps1 (run by hand); Entra-only authentication is the access control
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

// The Entra administrator is what creates the contained users (infra/bootstrap-sql.ps1).
resource sqlEntraAdmin 'Microsoft.Sql/servers/administrators@2023-08-01' = {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: entraAdminLogin
    sid: entraAdminObjectId
    tenantId: entraTenantId
  }
}

resource sqlEntraOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01' = {
  parent: sqlServer
  name: 'Default'
  dependsOn: [ sqlEntraAdmin ]
  properties: {
    azureADOnlyAuthentication: entraOnlyAuthentication
  }
}

// No firewallRules resource is declared on purpose. infra/sync-sql-firewall.ps1 (run by hand through sync-sql-firewall.yml; since
// 2026-11-19 neither the deploy nor a schedule runs it) makes the rules exactly the environment's outbound addresses, named aca-outbound-<n>,
// plus the "Allow Azure services" rule (kept since 2026-11-21), and deletes everything else, including the legacy "Allow anyone" rule.

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368
    minCapacity: json('0.5')
    // autoPauseDelay is not declared, on purpose. On the free offer the database pauses after 15 minutes without a session and
    // "the auto-pause delay is fixed and can't be changed" (Microsoft Learn, Azure SQL Database free tier, Considerations). This
    // file used to send 60: Azure stored and reported 60 and still paused after 15 (measured 2026-11-20: 15 min 45 s after the last
    // session closed). Each resume takes 42 to 58 s and uses about 1,000 of the month's 100,000 free vCore-seconds.
    useFreeLimit: true
    // Never bills: when the month's allowance is used up the database stays paused until the 1st (logins get SQL error 42119, which
    // the API answers as "database_resting" and the app shows as "back on the 1st").
    freeLimitExhaustionBehavior: 'AutoPause'
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
    readScale: 'Disabled'
  }
}

resource sqlBackup 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: sqlDatabase
  name: 'default'
  properties: {
    retentionDays: backupRetentionDays
    diffBackupIntervalInHours: 12
  }
}

// ---------------------------------------------------------------- auditing (INFRA-07)
// Server audit events go to a Log Analytics workspace. The first 5 GB a month are free, retention up to 31 days is
// free, and the daily cap below stops ingestion at 0.1 GB a day (3 GB a month), so this can never bill. Only
// sign-ins, failed sign-ins and schema, principal and role changes are audited; the per-row "who changed data" trail is
// the application's AuditLog table. Defender for SQL (paid) stays off.

resource auditWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'lifestream-audit'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: auditRetentionDays
    workspaceCapping: { dailyQuotaGb: json('0.1') }
    features: { disableLocalAuth: false }
  }
}

resource sqlAuditing 'Microsoft.Sql/servers/auditingSettings@2023-08-01' = {
  parent: sqlServer
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
    retentionDays: 0
    auditActionsAndGroups: [
      'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
      'FAILED_DATABASE_AUTHENTICATION_GROUP'
      'SCHEMA_OBJECT_CHANGE_GROUP'
      'DATABASE_PRINCIPAL_CHANGE_GROUP'
      'DATABASE_ROLE_MEMBER_CHANGE_GROUP'
    ]
  }
}

resource sqlMaster 'Microsoft.Sql/servers/databases@2023-08-01' existing = {
  parent: sqlServer
  name: 'master'
}

resource sqlAuditToWorkspace 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'lifestream-sql-audit'
  scope: sqlMaster
  dependsOn: [ sqlAuditing ]
  properties: {
    workspaceId: auditWorkspace.id
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true }
    ]
  }
}

// ---------------------------------------------------------------- delete locks (INFRA-08)

resource sqlServerLock 'Microsoft.Authorization/locks@2020-05-01' = if (enableLocks) {
  name: 'lifestream-server-no-delete'
  scope: sqlServer
  properties: {
    level: 'CanNotDelete'
    notes: 'Lifestream SQL server. Remove this lock deliberately before deleting.'
  }
}

resource sqlDatabaseLock 'Microsoft.Authorization/locks@2020-05-01' = if (enableLocks) {
  name: 'lifestream-database-no-delete'
  scope: sqlDatabase
  properties: {
    level: 'CanNotDelete'
    notes: 'BloodBank database. Remove this lock deliberately before deleting.'
  }
}

// ---------------------------------------------------------------- Container Apps (INFRA-09, OWN-02)

resource acaEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'lifestream-env'
  location: location
  properties: {
    // appLogsConfiguration is omitted on purpose: Azure rejects destination 'none' and treats no property as no log destination, so nothing is ingested or billed. No vnetConfiguration, so no public IP or load balancer is billed.
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
    zoneRedundant: false
  }
}

var sqlConnectionBase = 'Server=tcp:${sqlFqdn},1433;Database=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Authentication=Active Directory Managed Identity'

// API version 2025-01-01 for this resource: it is the first stable one with scale.cooldownPeriod.
resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'lifestream-api'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    environmentId: acaEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 1
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false // plain HTTP is redirected to HTTPS
        traffic: [ { latestRevision: true, weight: 100 } ]
        // Only Cloudflare reaches the container (the Worker on api.lifestream.himaiz.com); every other address gets 403 from the ingress.
        ipSecurityRestrictions: [for (range, i) in ingressAllowedRanges: {
          name: 'cloudflare-${i + 1}'
          action: 'Allow'
          ipAddressRange: range
          description: 'Cloudflare IPv4 range'
        }]
      }
      secrets: [
        { name: 'jwt-signing-key', value: jwtSigningKey }
        { name: 'origin-auth', value: originAuthSecret }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          // Size and cooldown are chosen together, against the Container Apps free grant (per subscription and month: 180,000
          // vCPU-seconds and 360,000 GiB-seconds; Microsoft Learn, Billing in Azure Container Apps). A replica is billed from its start
          // until it is removed, which is the cooldown plus about 135 s after the last request (measured: 7 min 15 s at 300 s).
          //   One session of 15 minutes: 30 s start + 900 s + 300 s + 135 s = 1,365 replica-seconds. One stray request: 465.
          //   The owner's scale, 5 sessions and 2 stray requests a day for 30 days: 232,650 replica-seconds a month.
          //   At 0.5 vCPU / 1 GiB that is 116,325 vCPU-seconds and 232,650 GiB-seconds: 65 % of each grant. Free up to 8 sessions a day.
          //   At 1 vCPU / 2 GiB it would be 129 %: not free. At 0.25 vCPU the first use of a new replica takes twice as long.
          // tests/infra/BicepTests.ps1 redoes this sum from the values below and fails when they leave the grant.
          // (With minReplicas 1 the size is paid around the clock: 0.5 vCPU would cost about 10 USD a month idle, 0.25 about 4.)
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            // System-assigned identity: no user, no password, no key anywhere in this string.
            { name: 'ConnectionStrings__Lifestream', value: sqlConnectionBase }
            { name: 'Jwt__SigningKey', secretRef: 'jwt-signing-key' }
            { name: 'Demo__Enabled', value: 'true' }
            // The public hostname is the Cloudflare Worker on api.lifestream.himaiz.com (edge/api); see OriginAuthMiddleware.
            { name: 'OriginAuth__Secret', secretRef: 'origin-auth' }
            { name: 'OriginAuth__Mode', value: originAuthMode }
          ]
          probes: [
            {
              // Liveness of the process only; /healthz never opens a database connection, so a cold database cannot fail it
              // and the probe never wakes the database. The process listens 1.0 to 1.6 s after its start and the first try comes
              // after 1 s, so it often misses; with a period of 1 s (the minimum) the next try is 1 s later, not 2. Sixty tries
              // keep the same minute of patience as before.
              type: 'Startup'
              httpGet: { path: '/healthz', port: 8080 }
              initialDelaySeconds: 1
              periodSeconds: 1
              timeoutSeconds: 3
              failureThreshold: 60
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        // The platform default, now written down because the free-grant sum above depends on it. A longer cooldown would spare a
        // returning visitor the 24 to 31 s container start, but not the database resume (the database pauses 15 minutes after its
        // last session whatever the replica does), and at 0.5 vCPU 600 s would already take 82 % of the grant.
        cooldownPeriod: 300
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '30' } } }
        ]
      }
    }
  }
}

// Runs the schema migration (EF Core) and creates the owner admin account. Started by the deploy workflow, never on a
// schedule, so it cannot wake the database by itself. It signs in as lifestream-migrator, a contained user with DDL
// rights and no password.
resource migrateJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'lifestream-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${migrator.id}': {} }
  }
  properties: {
    environmentId: acaEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      secrets: [
        { name: 'owner-admin-password', value: ownerAdminPassword }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: apiImage
          args: [ '--migrate' ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ConnectionStrings__Lifestream', value: '${sqlConnectionBase};User Id=${migrator.properties.clientId}' }
            { name: 'Owner__AdminPassword', secretRef: 'owner-admin-password' }
          ]
        }
      ]
    }
  }
}

// ---------------------------------------------------------------- budget alert (INFRA-09)

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = {
  name: 'lifestream-monthly'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [ ownerEmail ]
      }
      actual100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [ ownerEmail ]
      }
    }
  }
}

// ---------------------------------------------------------------- outputs

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output apiName string = api.name
output apiPrincipalId string = api.identity.principalId
output migratorName string = migrator.name
output migratorPrincipalId string = migrator.properties.principalId
output migratorClientId string = migrator.properties.clientId
output deployerClientId string = deployer.properties.clientId
output sqlServerFqdn string = sqlFqdn
