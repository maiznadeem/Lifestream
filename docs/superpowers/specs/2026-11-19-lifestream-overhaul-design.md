# Lifestream 2.0 overhaul: design

Date: 2026-11-19. Status: draft for owner approval.
Inputs: five audits of the current app (security 19 findings, performance 17, code 52, UI/UX 50, plus stack research),
merged into 157 testable requirements (`run/audit/requirements.json`, mirrored in `docs/requirements.md` once committed).

## 1. Intent

Lifestream is a blood-bank management desktop app that began as a Database Systems course project and is public on
GitHub. The owner wants it overhauled so that it is **secure, very fast, and easy to read and manage**, and then shipped
as a new version on the GitHub Releases page. Anyone who downloads it should be able to sign in with a public demo
account and use every screen against realistic, clearly synthetic data, without being able to break it for the next
visitor and without the owner paying anything while nobody is using it.

## 2. Goals, non-goals, anti-goals

**Goals**
- No credential in any shipped artifact or reachable from the public repo; a server-side API is the only thing that
  talks to the database, through a managed identity.
- Every audit finding fixed or recorded as moot (157 requirements, each with a proof).
- Perceived speed: the window never freezes; every screen shows a skeleton within 100 ms and real data from one API
  round trip; lists are paged and filtered on the server.
- Skeleton loaders on every data surface and full button states (idle, hover, pressed, focus, busy, disabled).
- Database schema, constraints and seed data as code, applied by automated migrations.
- Automated CI, deploy and release from GitHub Actions; only the newest 3 API images kept.
- Latest stable .NET: **.NET 11** (GA 2026-11-10, supported to 2028-11-14).

**Non-goals**
- A real production deployment for a real blood bank (no separate production database, no clinical certification).
- A web or mobile client. Multi-tenant or multi-site support. Offline mode.
- Light theme (dark only for 2.0; tokens make a light theme a later, contained change).
- Paid code-signing certificate (see owner question 3).

**Anti-goals (a failure even if it works)**
- Any idle cost in Azure, or a design that wakes the database on a timer (the free database offer stops for the rest of
  the month when its allowance runs out).
- A desktop binary that contains a secret.
- A rewrite that loses a feature the old app had (every old screen's capability must exist in 2.0).
- Hard-coded numbers shown as data (the old dashboard's 2023 range, the broken gender counts).

## 3. Constraints

- Azure: subscription `Default Subscription` (88f308cf-...), resource group **Lifestream** (West Europe), existing server
  `lifestream-server` with serverless free-offer database `BloodBank` (auto-pause, `AutoPause` on free-limit exhaustion).
  Everything new goes into the same resource group and must cost $0 at no traffic.
- Free allowances (from research, Nov 2026): Container Apps 180,000 vCPU-s, 360,000 GiB-s, 2 M requests per month;
  SQL free offer 100,000 vCore-s and 32 GB per database per month. A paused database resumes on login in under a minute;
  the first attempt fails with error 40613.
- Public repo, public demo: assume every visitor is hostile.
- Windows desktop only; WPF on .NET 11 (no trimming, no Native AOT for WPF; ReadyToRun is the startup lever).
- Old credentials: the two old servers are deleted (DNS no longer resolves); the live server's admin password was
  rotated on 2026-11-18 and is held only in the owner's local secret store (`lifestream-sql-admin`).

## 4. Approaches considered

| Option | Trade-off | Decision |
|---|---|---|
| **A. API on Azure Container Apps, Consumption, scale to zero; image on GHCR** | $0 idle; free grant covers demo use; fastest when warm; ~5–25 s cold start | **Chosen by owner** |
| B. API on App Service Free (F1) | $0 always, never bills; slow shared CPU; 60 CPU-min/day cap stops the API | Rejected |
| C. Azure Functions Flex | needs a storage account (cents per month, not $0) | Rejected |
| D. No server; per-staff Entra logins to SQL | every visitor would need an account in the owner's directory | Rejected |

Within A, the client-side choices (my calls, stated for correction): WPF on .NET 11 rather than a new UI framework,
because the owner asked to overhaul this app and WPF-UI gives a Fluent look with little custom chrome; EF Core 11 with
migrations rather than hand-written SQL scripts, because it gives typed queries, generated migrations and idempotent
scripts in one tool; LiveCharts2 rather than the abandoned LiveCharts 0.9.7.

## 5. Architecture

```
 Lifestream.Desktop (WPF, .NET 11, self-contained, ReadyToRun, Velopack installer + auto-update)
        |  HTTPS + JWT (15 min access, rotating refresh)
        v
 Lifestream.Api (ASP.NET Core 11 minimal API) on Container Apps: min 0, max 1 replica, 0.25 vCPU / 0.5 GiB,
        |   no log destination; system-assigned managed identity; startup probe /healthz (no DB)
        |   plain Consumption environment, no VNet (a VNet bills a static public IP and load balancer, about $25 a month idle)
        v   outbound from the environment's shared public addresses
 Azure SQL lifestream-server / BloodBank (serverless free offer, Entra-only auth,
        firewall: only the environment's outbound IPs, kept in sync by infra/sync-sql-firewall.ps1)
        ^
        |  EF Core migration bundle run as a Container Apps Job (manual trigger, separate identity with DDL rights)
 GitHub Actions (OIDC to Azure, no stored cloud secret): CI -> image to GHCR -> migrate job -> update app -> prune images
```

### 5.1 Solution layout (one repo, replaces the old project at the repo root)

```
Lifestream.slnx
Directory.Build.props        nullable, warnings as errors, analyzers, LangVersion latest, net11.0
Directory.Packages.props     central package versions
src/
  Lifestream.Domain/         enums (BloodType, Gender, Shift, Role, statuses), rules (eligibility, FIFO issue, expiry)
  Lifestream.Contracts/      request/response records shared by API and client; enum lists for dropdowns
  Lifestream.Data/           EF Core DbContext, entity configs, migrations, demo seed generator, 100x generator
  Lifestream.Api/            endpoints by feature folder, auth, validation, problem details, demo reset, health
  Lifestream.Client/         view models, services, API client (no WPF reference, fully unit-testable)
  Lifestream.Desktop/        WPF views, design system (tokens, controls, skeletons), app host, Velopack bootstrap
tests/
  Lifestream.Domain.Tests/   Lifestream.Api.Tests/ (WebApplicationFactory + real SQL Server)
  Lifestream.Client.Tests/   Lifestream.Desktop.Tests/ (STA off-screen renders, component states, captures)
  perf/                      benchmark script: 1x and 100x datasets, timings written to a results file
infra/                       main.bicep (+ parameters), scripts to bootstrap Entra admin and database users
.github/workflows/           ci.yml, deploy-api.yml, release.yml, codeql/secret-scan
docs/                        requirements.md, architecture, runbook, data retention, screenshots (generated)
```

Feature folders mirror each other across Api, Client and Desktop (`Donors/`, `Patients/`, `Appointments/`,
`Requests/`, `Receipts/`, `Stock/`, `Staff/`, `Dashboard/`, `Profile/`, `Auth/`), so one feature is easy to find end to
end. The old `WPFApp` project, `publish/`, the `.bacpac`, `.rar`, SQL backups and screenshots move out of the repo
(screenshots are regenerated from the demo by script).

### 5.2 Database

- New clean schema created by EF Core migrations from an empty database: singular English table names, `Id` int
  identity keys, foreign keys on every reference (Receipt gets them), indexes on every filtered, sorted or joined
  column, unique case-insensitive `Staff.Username`, check constraints kept for every enum and status, UTC `datetime2`
  for timestamps, `date` for date-only values, `rowversion` for optimistic concurrency on editable rows.
- Staff credentials: `PasswordHash` nvarchar(256) (ASP.NET Core Identity `PasswordHasher`, PBKDF2-HMAC-SHA512, iteration
  count raised to at least 600,000), lockout fields, refresh tokens table (hashed). Photos in `StaffPhoto` (<= 30 KB,
  256 px), never read with Staff rows.
- `BloodBag` keeps stock: one row per unit with blood type, collected date, expiry date (collected + 42 days, one
  configuration value), status (Available, Issued, Discarded), the donation receipt that created it (required) and the
  issue receipt that consumed it (nullable). "Expiring soon" = within 7 days. Issue is oldest-unexpired-first, in a
  transaction.
- `Receipt` is one table with `Kind` = `Donation` or `Issue` (my call; it keeps one receipts screen and one printable
  receipt). `StaffId` is always required and comes from the token. A check constraint makes `AppointmentId` required
  and `RequestId` null for a donation, and the reverse for an issue; both columns have foreign keys and indexes.
  Unique index on `AppointmentId` (filtered, not null) so an appointment can be receipted once.
- No triggers, no SQL functions: the old `countblood`, `GetGenderCounts` (a Cartesian join that reported 595 male
  donors instead of 17), `BloodStock`, etc. become tested LINQ queries in the API. The old trigger's donor/appointment
  mix-up disappears with it.
- `Attendance` is dropped (no screen ever used it). `AuditLog` records user id, action, entity, id, UTC time.
- Demo data: a deterministic generator in `Lifestream.Data` produces clearly synthetic people (generated names, phone
  numbers in a reserved fictional range) with dates relative to "today", so the demo always looks current. A 100x
  generator feeds the performance tests.
- Existing Azure data: exported once to a local `.bacpac` in the owner's archive folder (never committed), then the
  database is migrated to the new schema and filled from the generator. No old row is carried over (staff rows held
  plain-text passwords; donor and patient rows are synthetic course data).

### 5.3 API

- Minimal API, feature folders, one `MapGroup` per resource, `RequireAuthorization` with policies `Staff`, `Admin`.
  Roles: `Admin`, `Staff`, and the demo account, which has the `Staff` role plus read-only access to staff management
  (it can view the Staff screen but not create, edit, delete or reset; it cannot change any password).
- Auth: `POST /auth/login`, `POST /auth/demo`, `POST /auth/refresh`, `POST /auth/logout`. Generic invalid-credential
  response; lockout after 5 failures (stored in the database, so it survives scale to zero); per-IP rate limits
  (login, demo, global) and body-size limits. JWT signing key is a Container Apps secret generated at first deploy.
- Lists: `GET /donors?search=&bloodType=&page=&pageSize=&sort=` and the same shape everywhere; page size 50, total
  count, server-side filter/sort, one query that joins display names (no per-row lookups, no `SELECT *`).
- Actions on records: `POST /appointments` (book; validates past dates, clinic hours, taken slots, eligibility with an
  override reason), `POST /appointments/{id}/donation` (records the donation: receipt + N bags + completes exactly that
  appointment, one transaction, a second receipt is rejected), `POST /requests/{id}/fulfil` (issues oldest bags first,
  rejects insufficient stock with no partial change), `PATCH` for partial updates that touch only supplied fields.
- `GET /dashboard?from=&to=` returns every block in one response (KPIs, monthly donations-vs-issued matrix with zeros,
  demographics including every gender value, stock alerts). `GET /stock` per blood type: available, expiring in 7 days,
  below-threshold flag.
- Errors: RFC 7807 problem details with a stable code and correlation id; never exception or SQL text. 404 for missing
  ids, 409 for conflicts and concurrency.
- Database waking: EF Core retry on transient errors (40613 and connect timeouts) for up to about 2 minutes, then 503
  with `Retry-After`. `/healthz` never touches the database; `/readyz` reports `database-waking` distinctly.
- **Demo reset without a timer:** the first authenticated request of each UTC day, while the database is already awake,
  takes an application lock (`sp_getapplock`) and, if `DemoState.LastResetDate < today`, deletes the demo data and
  re-inserts the generator's output in one transaction. Exactly once under concurrency. The owner's admin account (see
  below) is never touched by the reset.
- Owner admin account: created by the migration job from a deployment secret (initial password generated by me, stored
  in the owner's local secret store as `lifestream-owner-admin`), never in the repo, excluded from the reset. It is a
  `Staff` row with `IsProtected = 1`; the reset rebuilds every table to the generator's output and keeps protected rows,
  so "after a reset every table equals the seed" holds with the seed defined as generator output plus protected rows.
- Abuse of the shared demo account (my calls): demo data can be messed up by a visitor until the next reset, so the
  demo account also gets a **"Reset demo data"** action (at most once per 10 minutes globally). Demo writes are capped
  (creates per IP per day, and creates are refused once a table exceeds its seed size by 500 rows). Demo login issues a
  token without touching the database. Rate limits per IP and a global request ceiling; max 1 replica; an Azure budget
  alert ($1, free) on the resource group. Each demo session gets its own refresh-token family, so concurrent visitors
  never log each other out.
- Known limitation, accepted for $0: someone who keeps the demo busy around the clock can use up the database's monthly
  free allowance, after which Azure pauses it until the next month. The API recognises that state and the client shows
  "The demo is resting until <1st of next month>" rather than an error. No timer or keep-alive ever wakes the database.
- Secrets live only in Azure, as Container Apps secrets set once by the orchestrator from the owner's local secret
  store: the JWT signing key (API) and the owner-admin initial password (migrate job). GitHub holds no cloud secret
  (OIDC). Key Vault is not used (not free at zero traffic).

### 5.4 Desktop client

- Generic host + DI, CommunityToolkit.Mvvm (source-generated properties and `AsyncRelayCommand`), WPF-UI 4.3
  `FluentWindow` (Snap Layouts, resize, proper caption buttons), LiveCharts2 for charts.
- Navigation service: one view model per visit, created by DI and set once as DataContext (today each screen builds
  its view model 2–3 times and loads data each time). Lists keep filters, sort and scroll for the session.
- Information architecture (from the UI/UX audit): grouped sidebar Overview (Dashboard), Donations (Donors,
  Appointments), Transfusions (Patients, Requests), Inventory (Stock, Receipts), Administration (Staff; hidden unless
  Admin or demo). Account menu top-right: Profile, Change password, Sign out. One screen per entity: list plus detail
  pane, create and edit in a drawer, actions start from the record (no typing raw database IDs anywhere: a searchable
  record picker replaces every ID box).
- Design system in `Desktop/Theme`: tokens from the UI/UX audit (neutral dark surfaces, brand red `#C8102E` as accent,
  every pair contrast-checked), 6-step type scale (Segoe UI Variable, bundled with Windows), 4-DIP spacing grid.
  Components: Button (primary, secondary, ghost, danger, icon) with idle, hover, pressed, focus, busy and disabled
  states (busy keeps its width, shows a spinner and the progressive verb, ignores repeat clicks); text input with
  persistent label and inline validation; record picker; data grid (virtualised, sortable, 36–40 DIP rows); skeleton
  loaders per surface (dashboard, list, detail, stock tiles, picker, profile) shown within 100 ms, shimmer 1.2 s, static
  when Windows animations are off, replaced in place without layout shift, switching to an error state after 10 s;
  empty and error states with an action; toasts; confirmation dialog for every delete (focus on Cancel, Esc cancels).
- Cold start: the API client sends `/healthz` at app launch (warming the container while the user types), and on a
  waking response shows "Waking up the server, this can take up to a minute" with skeletons, retrying with backoff for
  up to 120 s, cancellable. One shared `HttpClient` (HTTP/2).
- Global exception handler (dispatcher, AppDomain, unobserved tasks): logs and shows a friendly dialog; nothing crashes
  the process. Login: empty fields, "Sign in" disabled until both are filled, a "Try the demo" button, no printed
  credentials.
- Dates through one formatter: `dd MMM yyyy` and `dd MMM yyyy HH:mm`.
- Accessibility: every control keyboard-operable with a visible 2 px focus ring, UI Automation names on every control,
  live regions for toasts and validation, charts with an equivalent table.

### 5.5 Performance budgets (each is a test or a benchmark with a threshold)

| Budget | Target |
|---|---|
| App launch to login window (warm OS cache, dev machine) | < 1.0 s |
| Any screen: skeleton visible after navigation | < 100 ms |
| Warm API, list endpoints at 100x data (local SQL Server) | p95 < 100 ms server time |
| Dashboard endpoint at 100x data | p95 < 100 ms, no cache needed (a cache, if any, holds at most 60 s) |
| API calls per screen load | exactly 1 per data set |
| UI thread gap during a load over a throttled link | < 50 ms |
| Staff list payload for 110 staff | < 20 KB, no image bytes |
| Grid with 10,000 items | < 300 row containers created |

### 5.6 Security

Server-side authorization on every endpoint (tested as a role-by-endpoint matrix); identity only from the token
(receipts take the staff id from the token); no password or hash in any response; masked password entry everywhere;
admin "Reset password" instead of ever showing one; CI scans every built artifact (UTF-8 and UTF-16) and the image for
credential patterns and fails on a hit; secret scanning and push protection on the repo; NuGet audit fails on
high-severity advisories; Dependabot; SQL: firewall reduced to the Container Apps environment's outbound IP addresses
(rules `aca-outbound-<n>`, kept equal to the environment's addresses by `infra/sync-sql-firewall.ps1`, run after every
API deploy by `deploy-api.yml` and weekly by `sync-sql-firewall.yml`; the "Allow anyone" rule and the all-Azure rule
removed), Entra-only authentication (the rotated SQL admin password stops working entirely), contained
users: API identity with read/write/execute only, migrator identity with DDL. Trade-off, stated plainly: there is no VNet, so
the allowed addresses are Azure egress addresses shared with other customers' Container Apps in the region. The firewall
narrows exposure but is not a tenant boundary; authentication is the real control (Entra-only, managed identity, least-privilege
contained users), which is why the sync script refuses to open the firewall until Entra-only is on (`infra/README.md`,
"Security trade-offs"). SQL auditing to a free target if one
exists, otherwise the application audit table plus Azure activity log (Defender for SQL is paid: off); delete locks on
the server and database.

### 5.7 Delivery

- `ci.yml` on every push and PR: build (warnings as errors), all tests (API tests against a SQL Server service
  container on Ubuntu; desktop tests on Windows), artifact credential scan, NuGet audit. Free for a public repo.
- `deploy-api.yml` on push to `master` touching API/Data/Contracts/infra: build single-arch image (provenance and SBOM
  off, so pruning cannot orphan manifests), push `ghcr.io/maiznadeem/lifestream-api:<sha>`, update and run the migrate
  job, update the container app, smoke-test `/healthz`, then `actions/delete-package-versions` keeping the newest 3.
- `release.yml` on tag `v*`: publish the desktop app self-contained win-x64 with ReadyToRun, pack with Velopack, upload
  installer, update packages and SHA-256 checksums to a GitHub Release. Version **2.0.0**.
- Old releases `Lifestream-1.0.0` and `Lifestream-1.0.1`: assets deleted (they contain the old admin connection string,
  now useless) and the release notes replaced with a pointer to 2.0.0.

## 6. Proof strategy

Every requirement becomes a ledger item proven by an automated test, a script or an off-screen capture, checked by an
independent verifier, and each proof is first run against a planted wrong expectation to show it can fail. Database
tests create a uniquely named database and drop it afterwards (the fixture verifies the drop). The test server comes
from `LIFESTREAM_TEST_SQL`; locally it defaults to the developer machine's SQL Server 2025 default instance
(`Server=localhost;Integrated Security=true;TrustServerCertificate=true`); in CI it is a
`mcr.microsoft.com/mssql/server:2025-latest` service container on Ubuntu. LocalDB is not used. Performance gates run
locally against the default instance and are reported, not enforced, in CI (shared runners are too noisy to gate on).
UI proofs render views off-screen in an STA test host (never on the owner's desktop) and save PNG captures.

The orchestrator, not a builder, runs every Azure change (export of the old database, Bicep deployment, the Entra admin
and contained-user bootstrap before the first migration, secrets, firewall, Entra-only switch); the infra builder only
writes and dry-runs (`what-if`) the templates and scripts.

**Stack spike first.** The skeleton step proves that a `net11.0-windows` app builds and runs headless tests with
WPF-UI 4.3, LiveCharts2, CommunityToolkit.Mvvm and a Velopack pack. Fallbacks if one fails: WPF-UI → WPF's built-in
Fluent theme (`ThemeMode`) with our own components; LiveCharts2 → ScottPlot.WPF; Velopack → zip with checksums.

## 7. Build order (modules that can run in parallel without touching each other)

1. **Skeleton** (one builder): solution, build props, central packages, `Contracts` (every DTO and endpoint shape from
   5.3), CI workflow, gate command. Contracts are then frozen; a change needs the orchestrator's written approval.
2. **Wave A, in parallel:** `db` (Data: schema, migrations, generators, tests) · `client-shell` (Desktop + Client: host,
   theme, components, skeletons, navigation, login, API client with cold-start handling, tested against a fake API) ·
   `infra` (Bicep, bootstrap scripts, deploy and release workflows; nothing applied to Azure by the builder).
3. **Wave B:** `api` (all endpoints on top of Data) · `client-features` part 1 (Dashboard, Donors, Patients, Stock).
4. **Wave C:** `client-features` part 2 (Appointments, Requests, Receipts, Staff, Profile) · performance pass.
5. **Deploy and release** (orchestrator): export the old database, apply infra, run migrations, deploy the API,
   end-to-end tests against Azure, tag v2.0.0, release, delete old assets, merge to master.

## 8. Owner questions (work proceeds on the defaults)

| # | Question | Default being built on | Where the switch is |
|---|---|---|---|
| 1 | Donor eligibility rule: age 18–60 and at least 90 days since the last donation, with an override reason? | Yes | `Domain/Eligibility.cs` constants |
| 2 | One database holding the demo data plus your private admin account (rather than a second database just for the demo)? | Yes, one database | `infra/main.bicep` parameter |
| 3 | Ship 2.0 unsigned (Windows shows a SmartScreen "More info → Run anyway" prompt) with published SHA-256 checksums? | Yes, unsigned | `release.yml` signing step |
| 4 | Dark theme only for 2.0? | Yes | `Desktop/Theme` |
| 5 | If GHCR creates the image package as private, can you flip it to public once in the GitHub UI (one click; there is no API for it)? | I'll ask at deploy time | — |

## 9. Left to the builder

Exact component internals, file names inside feature folders, test naming, Bicep resource names (prefix `lifestream-`),
the synthetic-data vocabulary, chart styling within the tokens, and any library choice not named above (each recorded
with a one-line reason in the builder's record).
