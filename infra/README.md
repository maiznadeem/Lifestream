# Infrastructure runbook

Everything Lifestream needs in Azure, as code. `main.bicep` is applied by the owner or orchestrator, never by CI (CI only
updates the app and runs the migrate job). Nothing in this folder contains a secret: personal and secret values come from
environment variables read by `main.bicepparam`.

## What gets built

| Resource (resource group `Lifestream`) | Notes |
|---|---|
| Container Apps environment `lifestream-env` | Plain Consumption environment: Consumption profile only, **no virtual network**, no log destination (`appLogsConfiguration` omitted; Azure rejects the string `none`). |
| Container app `lifestream-api` | External HTTPS only (port 8080, plain HTTP is redirected), min replicas 0, max 1, 0.5 vCPU / 1 GiB, cooldown 300 s, startup probe `/healthz` every second, system-assigned identity, secret `jwt-signing-key`. |
| Container Apps job `lifestream-migrate` | Manual trigger only. Identity `lifestream-migrator` (user-assigned, no password). Secret `owner-admin-password`. |
| SQL server `lifestream-server` and database `BloodBank` | Declared so the settings are code: free offer (`useFreeLimit`, `AutoPause`; the offer pauses the database after 15 minutes without a session, which is fixed and not a setting), Entra administrator, Entra-only switch, auditing, locks, backup policy. No firewall rule is declared: `sync-sql-firewall.ps1` owns them (see below). |
| `lifestream-audit` Log Analytics workspace | Receives SQL sign-in and schema-change audit events. 31-day retention, daily cap 0.1 GB, so it stays inside the free 5 GB a month. |
| `lifestream-deployer` identity | GitHub Actions signs in as this identity through an OIDC federated credential for `maiznadeem/lifestream` branch `master`. Contributor on the resource group only. No secret. |
| Budget `lifestream-monthly` | 1 per month, email alerts at 50% and 100% actual. An alert does not stop spending; the design relies on scale-to-zero and the free offer for that. |

### Why nothing here bills while there is no traffic (OWN-02)

No custom virtual network: a VNet-integrated environment bills a static public IP and a load balancer around the clock
(Learn, "Virtual network configuration", managed resources), so the design uses a plain environment and a firewall that follows the
environment's outbound addresses instead. Every remaining resource type is on the reviewed allow-list in `tests/infra/BicepTests.ps1`
(the build fails on any type outside it). Sources, Microsoft Learn, read 2026-11-19:

- *Billing in Azure Container Apps* (`azure/container-apps/billing`): the Consumption plan bills vCPU-seconds, GiB-seconds and HTTP
  requests; a revision scaled to zero replicas incurs no resource charge; the first 180,000 vCPU-s, 360,000 GiB-s and 2 million
  requests a month per subscription are free; jobs bill only while an execution runs; the Dedicated plan management fee applies only to
  Dedicated profiles, private endpoints or planned maintenance, none of which are used. Environment, app and job: $0 idle.
- *Log Analytics workspace overview* (`azure/azure-monitor/logs/log-analytics-workspace-overview#cost`) and *Azure Monitor Logs cost
  calculations* (`.../logs/cost-logs`): no cost for creating or keeping a workspace, charges are for ingested data and retention only;
  Analytics retention up to 31 days is included; the first 5 GB a month per billing account is free. With no ingestion the workspace is $0;
  the daily cap `workspaceCapping.dailyQuotaGb` 0.1 bounds ingestion at about 3 GB a month, inside the free grant.
- *Managed identities for Azure resources*: free with Microsoft Entra ID. Federated credentials, role assignments and locks have no charge.
- *Tutorial: Create and manage budgets*: a budget only notifies; it creates no cost.
- The database is on the free offer with `AutoPause` on exhaustion (INFRA-10), so it stops for the month instead of billing.

No container registry (images live in GHCR), no Key Vault, no gateway, no private endpoint, no Redis, no dedicated plan, no storage account.

## Two facts that shaped the template

- **Region.** `lifestream-server` is in **eastus** while the resource group is in westeurope, so the apps are created in eastus
  (parameter `location`) next to the database.
- **No VNet, so the firewall follows the outbound addresses.** The API and the migrate job reach Azure SQL from the environment's
  outbound public addresses, which Azure may change. `infra/sync-sql-firewall.ps1` makes the server's firewall exactly those addresses
  (`aca-outbound-<n>`), deleting every other rule including "Allow anyone" and `AllowAllWindowsAzureIps`. It runs **by hand only**
  (`sync-sql-firewall.yml`, Run workflow, or the script itself), and it never touches the database. Since 2026-11-19 neither
  `deploy-api.yml` nor a schedule runs it: the Consumption environment's shared outbound pool is 170+ addresses, and adding them one
  rule at a time took longer than the deploy job. Entra-only authentication is the access control (see Security trade-offs). It adds or
  updates first, deletes second. It fails, deleting nothing, if the
  environment reports no addresses. A CanNotDelete lock on the server also blocks deleting firewall rules, so when rules must be deleted
  and such a lock exists the script removes the lock, deletes, and puts the identical lock back in a `finally` block (`UNLOCK` and
  `RELOCK` lines in the log). That needs `Microsoft.Authorization/locks/delete` and `write`, which the built-in **Contributor** role
  (the `lifestream-deployer` identity) does not have: with Contributor the script fails loudly at the lock, deletes nothing, and the
  addresses the API needs are already in place. Granting the deployer Owner or User Access Administrator just for this would be a far
  bigger right than the problem deserves, so the supported paths are: the runbook (steps 3 and 8 do the one-time cleanup with locks off),
  and, if Azure ever shrinks the outbound set, deleting the stale `aca-outbound-<n>` rule once by hand as the subscription owner. The script
  also refuses to add any outbound-address rule while Entra-only authentication is off (see Security trade-offs).
- **Backups.** The free offer with auto-pause on exhaustion limits point-in-time restore to **7 days** (Microsoft Learn,
  "Azure SQL Database free tier FAQ"); the live policy is already 7. Requirement INFRA-08 asks for 14 or more and cannot be met on
  the free offer; `backupRetentionDays` defaults to 7 and Azure rejects a larger value. Moving to a paid database to get 14 days
  is an owner decision (it ends the free offer).

## Order of operations (orchestrator)

Each step lists what it needs and how to check it. Steps that change Azure are the only ones that do.

1. **Register the resource providers** (one-time; the subscription has Sql, Authorization and Consumption only):
   `az provider register --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 --namespace Microsoft.App`, `Microsoft.ManagedIdentity`, `Microsoft.Insights`,
   `Microsoft.OperationalInsights` (no `Microsoft.Network`: the template has no network resource), then wait until
   `az provider show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -n <ns> --query registrationState` is `Registered`.
   Until then a what-if of the full template fails with `Conflict` on the first unregistered provider.
2. **Fill the environment** in the shell that runs the deployment (values from the local secret store; never echo them):

   | Variable | Source |
   |---|---|
   | `LIFESTREAM_API_IMAGE` | `ghcr.io/maiznadeem/lifestream-api:<sha>` of an image already pushed (run the build/push part of `deploy-api.yml` once, or push by hand). The GHCR package must be public (flip once in the GitHub UI), or the app cannot pull it. |
   | `LIFESTREAM_ENTRA_ADMIN_LOGIN`, `LIFESTREAM_ENTRA_ADMIN_OBJECT_ID` | The Entra user or group that administers the SQL server (the identity choice is yours; a group is better than a person). |
   | `LIFESTREAM_OWNER_EMAIL` | Budget alert recipient. |
   | `LIFESTREAM_JWT_SIGNING_KEY` | Generate 48+ random bytes (base64) once. Keep it in the secret store. |
   | `LIFESTREAM_OWNER_ADMIN_PASSWORD` | Initial password of the owner admin account (`lifestream-owner-admin` in the store). |

3. **Dry run**, then apply, which deploys the app with its managed identity (locks off for now because step 8 deletes firewall rules,
   which a lock on the server blocks):
   ```
   az deployment group what-if --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 --resource-group Lifestream --parameters infra/main.bicepparam
   $env:LIFESTREAM_ENABLE_LOCKS = 'false'
   $env:LIFESTREAM_ENTRA_ONLY = 'false'   # bootstrap only: the template default is true, so a later apply can never re-enable SQL logins
   az deployment group create --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 --resource-group Lifestream --parameters infra/main.bicepparam
   ```
   Read `outputs.apiPrincipalId`, `outputs.migratorPrincipalId`, `outputs.deployerClientId`.
   Never use `--mode Complete`: it would try to delete the `master` database and is not needed.
4. **Create the contained database users** (Windows PowerShell 5.1 or pwsh; signed in with `az login` as the Entra administrator;
   the legacy firewall rules are still open at this point, which is what lets your machine and the environment connect):
   `./infra/bootstrap-sql.ps1 -ApiPrincipalId <apiPrincipalId> -MigratorPrincipalId <migratorPrincipalId>`.
   It prints `OK: lifestream-api holds db_datareader, db_datawriter and EXECUTE only` or lists every problem. If `CREATE USER ...
   FROM EXTERNAL PROVIDER` is refused, give the SQL server a system-assigned identity and grant it the Entra *Directory Readers*
   role, then rerun (the script is idempotent).
5. **Prove the managed identity login**: run the migration once, from the GitHub workflow (push to `master`, or *Run workflow*; its
   firewall-sync step will refuse until step 7, which is expected) or by hand:
   `az containerapp job start --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -n lifestream-migrate -g Lifestream`. The job must exit 0 (the migrator signed in
   with its identity), and one API request that reads the database must succeed (the API signed in with its identity).
6. **GitHub settings** (owner, once): repository variables `AZURE_CLIENT_ID` (= `deployerClientId`), `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`; secret scanning and push protection on; Dependabot on. These ids are not secrets.
7. **Enable Entra-only authentication**, only after step 5 succeeded:
   ```
   ./infra/enable-entra-only.ps1 -SubscriptionId 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -ResourceGroup Lifestream -Server lifestream-server -ManagedIdentityLoginProven
   ```
   The script refuses without that switch or without an Entra administrator, changes nothing if it is already on, and fails unless
   `az sql server ad-only-auth get --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65` then reports true. SQL logins, including the old admin login, stop working.
8. **Make the firewall hold only the API's outbound addresses** (a template cannot delete what it does not declare, so a script does it):
   ```
   ./infra/sync-sql-firewall.ps1 -SubscriptionId 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -ResourceGroup Lifestream -Server lifestream-server -Environment lifestream-env
   az sql server firewall-rule list --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -s lifestream-server    # only aca-outbound-<n> rules
   ```
   The script refuses to add any outbound-address rule unless Entra-only authentication reads true back from the server (step 7), because the
   addresses are shared Azure egress (see Security trade-offs). It deletes "Allow anyone" and `AllowAllWindowsAzureIps`. After this only
   the environment's outbound addresses can reach the database, so nothing on your own machine can: run any further SQL tooling from
   the job. If the environment lists no outbound address (no app has started), the script stops and changes nothing. From here on,
   run `sync-sql-firewall.yml` by hand when the environment's outbound addresses change: neither the deploy nor a schedule runs it.
9. **Locks, and the template in step with reality**: rerun step 3 with `LIFESTREAM_ENABLE_LOCKS=true` and
   `Remove-Item Env:LIFESTREAM_ENTRA_ONLY` (the template and parameter file default to Entra-only on; never set it to false again), with `LIFESTREAM_API_IMAGE` set to the **currently running** image
   (`az containerapp show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -n lifestream-api -g Lifestream --query properties.template.containers[0].image`),
   because the deploy workflow moves the app to newer images and the template would otherwise move it back.

## Security trade-offs

- **The firewall narrows exposure; it is not a tenant boundary.** A plain Consumption environment has no virtual network, so the API
  reaches Azure SQL from outbound public addresses that Azure owns and shares with other customers' Container Apps in the region.
  Allow-listing them keeps out the rest of the internet and the "all Azure services" rule, but another tenant's container could connect
  from the same address and reach the login endpoint.
- **Authentication is the real control.** Entra-only authentication (no SQL logins, no passwords to guess or leak) and the
  least-privilege contained users (`lifestream-api` reads and writes data only; `lifestream-migrator` is the only DDL identity) are what
  keep other tenants out. That is why `sync-sql-firewall.ps1` refuses to open the firewall to the outbound addresses until Entra-only is on.
- **Paid alternatives, and what they cost while idle** (all rejected for OWN-02, "costs nothing while there is no traffic"; Microsoft Learn,
  read 2026-11-19): a custom virtual network with a SQL service endpoint or private networking bills a static public IP and a load
  balancer around the clock (`container-apps/custom-virtual-networks#managed-resources`); a NAT gateway for a fixed outbound address
  bills per hour plus its public IP (`container-apps/networking`: NAT gateway needs a workload profile environment with a VNet); a
  private endpoint to the SQL server bills per hour, and in Container Apps private endpoints also incur the Dedicated Plan Management
  charge (`container-apps/billing`). Moving to any of them is an owner decision that ends the zero idle cost.

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
| OWN-02, INFRA-09 | `pwsh tests/infra/run.ps1` (allow-list of idle-free resource types over the compiled template, no VNet); `az containerapp show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -n lifestream-api -g Lifestream` shows `minReplicas 0`, `maxReplicas 1`, a `Startup` probe on `/healthz`, ingress `external` with `allowInsecure false`; `az consumption budget list --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65` shows `lifestream-monthly`. |
| OWN-03, INFRA-12 | After a deploy, `tools/prune-image-versions.ps1` keeps the newest 3 (refusing to delete the deployed image) and `tools/verify-image-versions.ps1` requires at most 3 versions, the deployed SHA among them and `docker manifest inspect` to pass; `tests/infra/ImageVersions.Tests.ps1`. |
| INFRA-01 | `tools/scan-artifacts.ps1` (see "Credential scanner" below) runs in `secret-scan.yml` (every tracked file of src, tests, infra and packaging; the desktop and API publish output and their logs; every layer of the API image, its configuration and the docker build log), in `deploy-api.yml` on every image layer and the build log before the image is pushed, in `release.yml` on the publish folder and its log, then on the installer, the update packages and the vpk log, and in `ci.yml` on the test logs and render captures before they are uploaded. `tests/infra/ScanRealArtifacts.Tests.ps1` builds the real Desktop publish and vpk Releases with a planted connection string (they fail) and clean (zero findings), and scans the API publish and the stock `mcr.microsoft.com/dotnet/aspnet:11.0` layers (zero findings). |
| INFRA-04 | `az sql server firewall-rule list --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -s lifestream-server` shows only `aca-outbound-<n>` rules, each a single address from the environment's `outboundIpAddresses`; `tests/infra/SqlFirewall.Tests.ps1`. |
| INFRA-05 | `az sql server ad-only-auth get --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -n lifestream-server` shows `azureAdOnlyAuthentication true` (`enable-entra-only.ps1` checks it); `tests/infra/EntraOnly.Tests.ps1`. |
| INFRA-06 | `bootstrap-sql.ps1` ends with its verification query returning no problems. |
| INFRA-07 | `az sql server audit-policy show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -n lifestream-server` returns `Enabled`; rows appear in `lifestream-audit` (`AzureDiagnostics | where Category == "SQLSecurityAuditEvents"`). The row-level "who changed data" trail is the API's `AuditLog` table. |
| INFRA-08 | `az lock list --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream` shows two `CanNotDelete` locks; `az sql db str-policy show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -s lifestream-server -n BloodBank` shows 7 days (see Backups above). |
| INFRA-10 | `az sql db show --subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65 -g Lifestream -s lifestream-server -n BloodBank` shows `useFreeLimit true` and `freeLimitExhaustionBehavior AutoPause`. |
| INFRA-13 | `deploy-api.yml` has `id-token: write`, no Azure secret, and the `deploy` job needs `image`, which needs `gate`. |
| INFRA-19 | `tools/generate-screenshots.ps1` (workflow `screenshots.yml`). |

## Credential scanner

`tools/scan-artifacts.ps1 -Path <files or folders>` exits 1 on a credential, 2 when it could not scan (missing path, no files, a read
error, a malformed allow-list) and 0 only when it looked at everything and found nothing. The rules live in `tools/scan-engine.cs`, which
the script compiles with PowerShell's own .NET runtime (no SDK needed on the runner).

- **Binary mode** (any file that is not plain text, and every member of a zip, nupkg, msix, tar, gzip, docker layer or an installer with
  an appended zip such as Velopack's `Setup.exe`, up to 6 levels deep): a credential keyword (password, passwd, pwd, user id, uid,
  accountkey, sharedaccesskey, client_secret, so also `MSSQL_SA_PASSWORD`, `DbPassword`) is looked for **anywhere**, in UTF-8 and in
  UTF-16LE and UTF-16BE at both byte alignments. Nothing depends on the byte before the keyword: in compiled .NET code that byte is a
  string length (the `#US` heap, custom-attribute blobs, `.resources`, BAML) and can be any value; three earlier designs failed exactly
  there. The decision is made on the right: `=` or `:` and a value that is not a placeholder, plus either a connection-string key
  (server, data source, initial catalog, database, user id, uid, address, addr) in the same string within 300 bytes, or a value of at
  least 6 characters that is not followed by more words (a help or log message after the separator is not a value).
- **.NET assemblies** are also read with `System.Reflection.Metadata`: every user string, custom-attribute blob, string constant and
  manifest resource (`.resources` strings, BAML and other embedded streams); the text rules run on every string found there, so a
  literal of any length is caught.
- **Text mode** (plain-text files on disk, logs, source): the keyword as a whole word and any value that is not a placeholder.
- **Values** can be quoted, ODBC brace-quoted or plain, with any characters (Cyrillic, the euro sign ...), and may start with any
  character, including the ones that look like operators (tilde, greater-than, equals, colon, comma, semicolon, backtick): a password
  key whose value begins with a tilde is a password. A value is skipped **only** when the whole value equals an entry of an exact list
  (`PlaceholderValues` in `scan-engine.cs`, pinned by a test): the format items zero to nine in braces, the three template variables for
  "Password" in the shell, Windows and PowerShell styles (`$(Password)`, `${PASSWORD}`, `%PASSWORD%`), `<password>`, three asterisks,
  and the empty value. Nothing that merely looks like a placeholder is skipped: a brace-quoted ODBC password is a password, and any
  other name in braces or percent signs, a `%s` format, `<your password>` or three-plus asterisks are values. Format strings and XAML
  markup in third-party files are reviewed and allow-listed by hash instead. A user id given as a `${reference}`, a `<description>` or a
  GUID names an identity and is not a secret; so is a C# interpolation hole (`{name}`) inside an interpolated string in source.
- **Text forms**: the Password, Pwd and Passwd keys and any prefixed key (`DbPassword`, `MSSQL_SA_PASSWORD`, `Database__Pwd`, dotted
  keys such as the Spring datasource password, command-line options such as `--db-password`) with an equals sign, the bare ODBC
  `UID`, `User ID`, a YAML key of that kind at the start of a line, the JSON pair form of those keys, and credentials in front of
  the host of a URL, for any scheme.
- **Compressed content**: gzip, zlib, brotli and raw deflate embedded resources (and `.br` and `.gz` files) are inflated and scanned as
  files of their own, as are the entries of a .NET single-file bundle (including `EnableCompressionInSingleFile`). Out of scope: UTF-32
  and Base64 (or otherwise encoded) values; the item asks for UTF-8 and UTF-16.
- **Second scanner: gitleaks** (pinned, `tools/fetch-gitleaks.ps1` downloads release 8.30.1 for Windows or Linux into the git-ignored
  `tools/.bin/` and verifies the SHA-256 published in the release's checksums file; no binary is committed). On every artifact scan
  `scan-artifacts.ps1` runs `gitleaks dir` with `tools/gitleaks-lifestream.toml` (all default rules plus database-credential rules for
  ADO.NET, ODBC and JDBC key=value pairs, JSON, YAML, environment and command-line keys and URL credentials, with the same exact
  placeholder list) over (a) each artifact path as it is (gitleaks opens archives up to the same depth) and (b) a text dump of every
  string the own extractor found: .NET user strings, attribute blobs, constants, `.resources` and BAML strings, the contents of
  decompressed resources, text archive members, and UTF-16 text decoded to UTF-8. The scan fails when **either** scanner finds something
  that is not allow-listed; it exits 2 when gitleaks cannot run. Gitleaks does not run in source mode (`-GitTracked`): `secret-scan.yml`
  scans the repository history with `gitleaks-action`. Third-party hits are allow-listed in `tools/gitleaks-allowlist.txt`
  (`file name|rule|sha256 of the matched text`, commented, never a product file), today the 61 reviewed texts of the stock layers.
  `-SkipGitleaks` exists for the own-engine unit tests only; a test fails if any workflow passes it.
- **Logs**: `tools/run-logged.ps1 -Log <file> -Command '<command>'` writes a command's output to a file, scans it, and prints it to
  the job log only after a clean scan (otherwise a redacted summary and exit 1). `ci.yml` runs every `dotnet build` and `dotnet test`
  through it, and `screenshots.yml` scans the whole upload folder before the upload.
- **Source tree**: `-GitTracked` scans what git lists (tracked files, plus new files that are not ignored), so ignored build output is
  never walked, while every tracked file is scanned and a tracked file under `bin`, `obj` or `TestResults` fails the scan. In C# an
  assignment from an identifier is code only where the keyword is in code; in any string literal or comment it is a finding.
  CI and a local run use the same command: `pwsh tools/scan-artifacts.ps1 -Path src,tests,infra,packaging -GitTracked`.
- **Test passwords** live in one file per test project, `tests/<Project>/TestSecrets.cs`; tests use its constants.
  `tests/infra/TestSecrets.Tests.ps1` fails on a password-shaped literal (a literal set on, initialised into or passed by name to
  anything named like a password, or a JSON password pair) in any other test file, so a new test cannot turn the scan red.
- **Allow-lists**, both reviewed by a person and both matched by hash, so an edit re-flags the text:
  `tools/secret-scan-allowlist.txt` (`path|rule|sha256 of the line`; only `tests/<Project>/TestSecrets.cs` lines, today none) and
  `tools/secret-scan-binary-allowlist.txt` (`file name|rule|sha256 of the matched text`) for benign strings in third-party files, today
  the 25 public strings of the stock aspnet:11.0 layers. Names starting with `Lifestream` or `appsettings` are refused. Nothing is
  exempted by path or by rule.
- **Images**: `tools/scan-image.sh <image>` scans `docker save` output, so each layer tar is scanned on its own and a file that one
  layer adds and a later layer deletes is still found, plus `docker image inspect` (baked environment variables) and the history.
- The matched value is never printed, and neither is a hash of it: in a public CI log a SHA-256 of a short value can be
  brute-forced. Findings and reports carry the file, rule, encoding, offset (and line). The hash an allow-list entry needs comes from a
  local run with `-ShowMatchHashes`, which the scanner refuses when `CI` or `GITHUB_ACTIONS` is set. A full Desktop publish (250 MB)
  scans in about 6 seconds with the own scanner and the Releases folder (1.1 GB, every member opened) in about 16; with gitleaks added about 17 and 35 seconds, and the 11 stock layers about 2 minutes.

## Every Azure CLI call passes the subscription

Scripts and this runbook always pass `--subscription 88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65` (the scripts take `-SubscriptionId`; the workflows pass
`vars.AZURE_SUBSCRIPTION_ID`), so nothing can run against whatever the CLI's default happens to be. `tests/infra` checks it.

## Tests

`pwsh tests/infra/run.ps1` runs the Bicep assertions and the idle-cost allow-list (`az bicep build`, no Azure access), the scanner,
bootstrap, firewall-sync, Entra-only and image-version tests (with a mocked `az`, `gh` and `docker`), screenshot and Store
packaging tests, static workflow assertions and `actionlint`. The scanner tests build real artifacts: tiny class libraries, the Desktop
publish and its vpk Releases (the .NET 11 SDK via `LIFESTREAM_DOTNET_ROOT` and `vpk` via `LIFESTREAM_VPK`, or both on PATH), and
download the stock aspnet:11.0 layer blobs (cached by digest in `LIFESTREAM_SCAN_CACHE` or the temp folder). Needed locally: Pester 5+, the `powershell-yaml` module, `actionlint`, the
Azure CLI with Bicep (for `az bicep build`, no login required) and optionally `shellcheck`. Point the runner at user-local installs with
`LIFESTREAM_PESTER_PATH`, `LIFESTREAM_YAML_MODULE`, `LIFESTREAM_ACTIONLINT` and `LIFESTREAM_SHELLCHECK_DIR`.
