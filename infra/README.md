# Infrastructure runbook

Everything Lifestream needs in Azure, as code. `main.bicep` is applied by the owner or orchestrator, never by CI (CI only
updates the app and runs the migrate job). Nothing in this folder contains a secret: personal and secret values come from
environment variables read by `main.bicepparam`.

## What gets built

| Resource (resource group `Lifestream`) | Notes |
|---|---|
| VNet `lifestream-vnet` 10.40.0.0/16, subnet `aca` 10.40.0.0/23 | Delegated to `Microsoft.App/environments`, service endpoint `Microsoft.Sql`. |
| Container Apps environment `lifestream-env` | Consumption profile only, VNet-integrated, log destination `none`. |
| Container app `lifestream-api` | External HTTPS only (port 8080, plain HTTP is redirected), min replicas 0, max 1, 0.25 vCPU / 0.5 GiB, startup probe `/healthz`, system-assigned identity, secret `jwt-signing-key`. |
| Container Apps job `lifestream-migrate` | Manual trigger only. Identity `lifestream-migrator` (user-assigned, no password). Secret `owner-admin-password`. |
| SQL server `lifestream-server` and database `BloodBank` | Declared so the settings are code: free offer (`useFreeLimit`, `AutoPause`), auto-pause 60 min, virtual network rule for the `aca` subnet, Entra administrator, Entra-only switch, auditing, locks, backup policy. |
| `lifestream-audit` Log Analytics workspace | Receives SQL sign-in and schema-change audit events. 31-day retention, daily cap 0.1 GB, so it stays inside the free 5 GB a month. |
| `lifestream-deployer` identity | GitHub Actions signs in as this identity through an OIDC federated credential for `MaizNadeem/Lifestream-WPF` branch `master`. Contributor on the resource group only. No secret. |
| Budget `lifestream-monthly` | 1 per month, email alerts at 50% and 100% actual. An alert does not stop spending; the design relies on scale-to-zero and the free offer for that. |

Idle cost is zero: no container registry (images live in GHCR), no Key Vault, no gateway, no private endpoint, no Redis, no
dedicated plan. The workspace is free under its cap.

## Two facts that shaped the template

- **Region.** Azure SQL virtual network rules only work inside the SQL server's own region. `lifestream-server` is in
  **eastus** while the resource group is in westeurope, so the network and the apps are created in eastus (parameter `location`).
- **Backups.** The free offer with auto-pause on exhaustion limits point-in-time restore to **7 days** (Microsoft Learn,
  "Azure SQL Database free tier FAQ"); the live policy is already 7. Requirement INFRA-08 asks for 14 or more and cannot be met on
  the free offer; `backupRetentionDays` defaults to 7 and Azure rejects a larger value. Moving to a paid database to get 14 days
  is an owner decision (it ends the free offer).

## Order of operations (orchestrator)

Each step lists what it needs and how to check it. Steps that change Azure are the only ones that do.

1. **Register the resource providers** (one-time; the subscription has Sql, Authorization and Consumption only):
   `az provider register --namespace Microsoft.App`, `Microsoft.Network`, `Microsoft.ManagedIdentity`, `Microsoft.Insights`,
   `Microsoft.OperationalInsights`, then wait until `az provider show -n <ns> --query registrationState` is `Registered`.
   Until then a what-if of the full template fails with `Conflict` on the first unregistered provider.
2. **Fill the environment** in the shell that runs the deployment (values from the local secret store; never echo them):

   | Variable | Source |
   |---|---|
   | `LIFESTREAM_API_IMAGE` | `ghcr.io/maiznadeem/lifestream-api:<sha>` of an image already pushed (run the build/push part of `deploy-api.yml` once, or push by hand). The GHCR package must be public (flip once in the GitHub UI), or the app cannot pull it. |
   | `LIFESTREAM_ENTRA_ADMIN_LOGIN`, `LIFESTREAM_ENTRA_ADMIN_OBJECT_ID` | The Entra user or group that administers the SQL server (the identity choice is yours; a group is better than a person). |
   | `LIFESTREAM_OWNER_EMAIL` | Budget alert recipient. |
   | `LIFESTREAM_JWT_SIGNING_KEY` | Generate 48+ random bytes (base64) once. Keep it in the secret store. |
   | `LIFESTREAM_OWNER_ADMIN_PASSWORD` | Initial password of the owner admin account (`lifestream-owner-admin` in the store). |

3. **Dry run**, then apply (locks off for now because step 4 has to delete child resources):
   ```
   az deployment group what-if --resource-group Lifestream --parameters infra/main.bicepparam
   $env:LIFESTREAM_ENABLE_LOCKS = 'false'
   az deployment group create --resource-group Lifestream --parameters infra/main.bicepparam
   ```
   Read `outputs.apiPrincipalId`, `outputs.migratorPrincipalId`, `outputs.deployerClientId`.
   Never use `--mode Complete`: it would try to delete the `master` database and is not needed.
4. **Create the contained database users** (Windows PowerShell 5.1 or pwsh; signed in with `az login` as the Entra administrator;
   the old firewall rules are still open at this point, which is what lets your machine connect):
   `./infra/bootstrap-sql.ps1 -ApiPrincipalId <apiPrincipalId> -MigratorPrincipalId <migratorPrincipalId>`.
   It prints `OK: lifestream-api holds db_datareader, db_datawriter and EXECUTE only` or lists every problem. If `CREATE USER ...
   FROM EXTERNAL PROVIDER` is refused, give the SQL server a system-assigned identity and grant it the Entra *Directory Readers*
   role, then rerun (the script is idempotent).
5. **Remove the legacy firewall rules** (a template cannot delete what it does not declare, and what-if does not list them):
   ```
   az sql server firewall-rule delete -g Lifestream -s lifestream-server -n "Allow anyone"
   az sql server firewall-rule delete -g Lifestream -s lifestream-server -n AllowAllWindowsAzureIps
   az sql server firewall-rule list   -g Lifestream -s lifestream-server    # must be empty
   ```
   After this only the `aca` subnet (virtual network rule `lifestream-aca`) can reach the database, so nothing on your own machine
   can: run any further SQL tooling from the job or add a temporary client-IP rule while the locks are off.
6. **Run the migration once** from the GitHub workflow (push to `master`, or *Run workflow*), or by hand:
   `az containerapp job start -n lifestream-migrate -g Lifestream`. The job must exit 0.
7. **GitHub settings** (owner, once): repository variables `AZURE_CLIENT_ID` (= `deployerClientId`), `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`; secret scanning and push protection on; Dependabot on. These ids are not secrets.
8. **Locks**: rerun step 3 with `LIFESTREAM_ENABLE_LOCKS=true` (or unset it).
9. **Entra-only, last**, after the API has been smoke-tested through its managed identity:
   `$env:LIFESTREAM_ENTRA_ONLY = 'true'` and rerun step 3. The old admin login stops working entirely. Re-apply with
   `LIFESTREAM_API_IMAGE` set to the **currently running** image (`az containerapp show -n lifestream-api -g Lifestream --query properties.template.containers[0].image`),
   because the deploy workflow moves the app to newer images and the template would otherwise move it back.

## Contract with the API image

The migrate job runs the same image as the API with the argument `--migrate` and these settings:

- `ConnectionStrings__Lifestream`: `...;Authentication=Active Directory Managed Identity;User Id=<migrator client id>`
- `Owner__AdminPassword`: from the secret `owner-admin-password`

`--migrate` must apply the EF Core migrations, create the protected owner admin account from `Owner__AdminPassword`, and exit 0 (non-zero on any failure).
The API container itself gets `ConnectionStrings__Lifestream` with `Authentication=Active Directory Managed Identity` (no user, no password),
`Jwt__SigningKey` from the secret `jwt-signing-key`, `Demo__Enabled=true` and listens on 8080.

## Checks that prove each requirement

| Item | Check |
|---|---|
| OWN-02, INFRA-09 | `az containerapp show -n lifestream-api -g Lifestream` shows `minReplicas 0`, `maxReplicas 1`, a `Startup` probe on `/healthz`, ingress `external` with `allowInsecure false`; `az consumption budget list` shows `lifestream-monthly`. |
| OWN-03, INFRA-12 | After a deploy, the last step of `deploy-api.yml` lists the package versions (at most 3), requires the deployed SHA among them and runs `docker manifest inspect` on it. |
| INFRA-01 | `tools/scan-artifacts.ps1` runs in `secret-scan.yml` (repo, desktop and API publish output, logs, API image), in `deploy-api.yml` before the image is pushed and in `release.yml` on the publish folder and the installer files. |
| INFRA-04 | `az sql server firewall-rule list` is empty; `az sql server vnet-rule list` shows `lifestream-aca`. |
| INFRA-05 | `az sql server ad-only-auth get -g Lifestream -n lifestream-server` shows `azureADOnlyAuthentication true`. |
| INFRA-06 | `bootstrap-sql.ps1` ends with its verification query returning no problems. |
| INFRA-07 | `az sql server audit-policy show` returns `Enabled`; rows appear in `lifestream-audit` (`AzureDiagnostics | where Category == "SQLSecurityAuditEvents"`). The row-level "who changed data" trail is the API's `AuditLog` table. |
| INFRA-08 | `az lock list -g Lifestream` shows two `CanNotDelete` locks; `az sql db str-policy show` shows 7 days (see Backups above). |
| INFRA-10 | `az sql db show` shows `useFreeLimit true` and `freeLimitExhaustionBehavior AutoPause`. |
| INFRA-13 | `deploy-api.yml` has `id-token: write`, no Azure secret, and the `deploy` job needs `image`, which needs `gate`. |
| INFRA-19 | `tools/generate-screenshots.ps1` (workflow `screenshots.yml`). |

## Tests

`pwsh tests/infra/run.ps1` runs the Bicep assertions (`az bicep build`, no Azure access), the scanner, bootstrap, screenshot and Store
packaging tests, static workflow assertions and `actionlint`. Needed locally: Pester 5+, the `powershell-yaml` module, `actionlint`, the
Azure CLI with Bicep (for `az bicep build`, no login required) and optionally `shellcheck`. Point the runner at user-local installs with
`LIFESTREAM_PESTER_PATH`, `LIFESTREAM_YAML_MODULE`, `LIFESTREAM_ACTIONLINT` and `LIFESTREAM_SHELLCHECK_DIR`.
