# Lifestream 2.0 Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> This run is executed with the `orchestrated-run` method: one builder per task in its own worktree, gates run by the
> orchestrator, an independent verifier per ledger item.

**Goal:** Replace the .NET Framework WPF app that talks straight to SQL with a .NET 11 WPF client, an ASP.NET Core 11
API on Azure Container Apps and an EF Core-migrated Azure SQL database, meeting the 167 ledger requirements, then ship
v2.0.0 on GitHub Releases.

**Architecture:** Desktop (WPF + CommunityToolkit.Mvvm + WPF-UI) → HTTPS/JWT → minimal API (Container Apps, scale to
zero, managed identity) → Azure SQL serverless free offer (Entra-only, VNet rule). Shared `Domain` and `Contracts`
projects pin every enum and DTO; feature folders mirror each other across Api, Client and Desktop.

**Tech Stack:** .NET 11 / C# 15, WPF, WPF-UI 4.3.0, LiveCharts2 (LiveChartsCore.SkiaSharpView.WPF 2.0.5),
CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting 11, ASP.NET Core 11, EF Core 11 (SqlServer), Azure.Identity,
xUnit v3, Bicep, GitHub Actions, Velopack 1.2.161, GHCR.

**Spec:** `docs/superpowers/specs/2026-11-19-lifestream-overhaul-design.md` (read it first). Requirements, each with
exact wording: `docs/requirements.md` (copied from the run's `audit/requirements.md` in Task 0). Ledger ids below
(`DB-xx`, `API-xx`, `SHELL-xx`, `FEAT-xx`, `INFRA-xx`, `OWN-xx`) refer to that file.

## Global Constraints

- Target `net11.0` (libraries, API) and `net11.0-windows` (Desktop); SDK pinned in `global.json` to the installed 11.0.x.
- `Nullable` enable, `TreatWarningsAsErrors` true, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild` true.
- Central package management (`Directory.Packages.props`); `NuGetAudit` with `NuGetAuditLevel` high fails the build.
- No secret, password, connection-string credential or token in the repo, in logs, or in any built artifact.
- Database access only through EF Core in `Lifestream.Data`, called only by `Lifestream.Api`. Client projects reference
  neither EF Core nor SqlClient.
- All timestamps UTC `datetime2`; date-only values `date`/`DateOnly`. Display format `dd MMM yyyy` and
  `dd MMM yyyy HH:mm` through one formatter.
- Blood shelf life 42 days; "expiring soon" 7 days; list page size 50, hard cap 200; access token 15 min.
- Donor eligibility: age 18–60, at least 90 days since last donation, override requires a reason.
- Demo reset: first authenticated request of each UTC day, `sp_getapplock`, protected rows kept; manual
  "Reset demo data" at most once per 10 minutes globally. No timer or keep-alive touches the database.
- Container Apps: min replicas 0, max 1, 0.25 vCPU / 0.5 GiB, logs destination none, startup probe `/healthz`.
- Keep only the newest 3 GHCR image versions; image is single-arch with provenance and SBOM disabled.
- UI tokens, type scale and component states exactly as in `run/audit/ui-ux.md` §4.2–4.3 (copied to
  `docs/design-system.md` in Task 2). Dark theme only.
- Test SQL Server from `LIFESTREAM_TEST_SQL`, default
  `Server=localhost;Integrated Security=true;TrustServerCertificate=true`; every test database is uniquely named and
  dropped by its fixture. Never open a UI window on the owner's desktop: UI tests render off-screen on an STA thread.
- Gate (run in the builder worktree and again on `overhaul` after merge):
  `dotnet build Lifestream.slnx -c Release` then `dotnet test Lifestream.slnx -c Release --no-build`.

## Review Focus

1. **Cold start while the user is mid-action**: a Save clicked while the API or database is waking must not
   double-submit or lose input; the button stays busy and the request is retried once with the same idempotency key.
   → Task 2 test `ApiClient_RetriesWakingResponse_WithSameIdempotencyKey`; Task 4 test
   `Post_WithRepeatedIdempotencyKey_ReturnsOriginalResult`.
2. **Two people editing the same record** (common with a shared demo account): the second save must get a clear
   "changed by someone else, reload" message, never silently overwrite. → Task 4 test
   `Patch_WithStaleRowVersion_Returns409Concurrency`; Task 5 test `DonorEdit_On409_ShowsReloadPrompt`.
3. **Demo reset mid-session**: a visitor whose records vanish at the daily reset must see "Demo data was reset" and a
   refreshed list, not 404 errors. → Task 4 test `Request_AfterReset_CarriesDemoResetHeader`; Task 2 test
   `Shell_OnDemoResetHeader_ShowsToastAndRefreshesCurrentScreen`.
4. **Clock and time zone at day boundaries**: an appointment at 23:30 local time and dashboard ranges ending "today"
   must land on the right day for a user whose local zone differs from UTC. → Task 4 test
   `Dashboard_RangeEndingToday_IncludesRecordAt2330Local`; Task 6 test `SlotPicker_ShowsLocalTimes`.
5. **Non-Latin and long names**: names such as "Ayşe Nur Öztürk-Abdulrahman" or 60-character names must save, search
   and display without truncation errors. → Task 1 test `Donor_NameWithDiacriticsAnd60Chars_RoundTrips`; Task 4 test
   `Search_IsAccentAndCaseInsensitive`.

---

### Task 0: Solution skeleton, stack spike, contracts, CI

**Ledger:** OWN-01, OWN-08, INFRA-14, INFRA-15, INFRA-16, INFRA-18, INFRA-20, API-45 (contracts part), DB-08 (naming).

**Files:**
- Delete: the old `WPFApp` project files, `publish/`, `BloodBank`, `BloodBank.bacpac`, `Frontend Screenshots/`,
  `Documentation.pdf` (archived first to `G:/Claude/workspace/lifestream/archive/2026-11-19/old-repo/` by the
  orchestrator, not the builder), `.gitignore` replaced.
- Create: `Lifestream.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`,
  `.gitignore`, `src/Lifestream.Domain/`, `src/Lifestream.Contracts/`, empty `src/Lifestream.Data/`,
  `src/Lifestream.Api/` (health endpoints only), `src/Lifestream.Client/`, `src/Lifestream.Desktop/` (spike window),
  the four `tests/*` projects, `.github/workflows/ci.yml`, `.github/dependabot.yml`, `docs/requirements.md`,
  `README.md` (rewritten: what it is, demo login, architecture, build, run), `docs/data-retention.md` (INFRA-20: synthetic data only; retention and deletion rule).

**Interfaces (Produces, frozen after this task; changes need the orchestrator's written approval):**
- `Lifestream.Domain` enums: `BloodType { APos, ANeg, BPos, BNeg, ABPos, ABNeg, OPos, ONeg }` with
  `BloodTypeExtensions.ToDisplay()` → `"A+"` etc. and `TryParseDisplay`; `Gender { Male, Female, Other }`;
  `Role { Admin, Staff }`; `Designation { Admin, Doctor, Worker, Assistant }` (the old app's values);
  `Shift { Morning, Evening, FullTime }`; `DonorFrequency { Regular, Occasional }`;
  `AppointmentStatus { Pending, Completed, Cancelled, NoShow }`; `RequestStatus { Pending, Fulfilled, Cancelled }`;
  `ReceiptKind { Donation, Issue }`; `BagStatus { Available, Issued, Discarded }`.
- `Lifestream.Domain` rules: `static EligibilityResult Eligibility.Evaluate(DateOnly dateOfBirth, DateOnly?
  lastDonation, DateOnly today)`; `record EligibilityResult(bool IsEligible, IReadOnlyList<string> Reasons, DateOnly?
  NextEligibleDate)`; `static class BloodRules { int ShelfLifeDays = 42; int ExpiringSoonDays = 7; }`.
- `Lifestream.Contracts`: `Optional<T>` (absent vs explicit null, with System.Text.Json converter);
  `record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)`; `static class ProblemCodes`
  (`validation`, `not_found`, `conflict`, `concurrency`, `insufficient_stock`, `already_receipted`, `slot_taken`,
  `ineligible`, `locked_out`, `rate_limited`, `database_waking`, `database_resting`, `demo_forbidden`,
  `demo_reset_cooldown`); `static class ApiRoutes` with every route string; `static class ApiHeaders`
  (`IdempotencyKey = "Idempotency-Key"`, `DemoReset = "X-Demo-Reset"`, `CorrelationId = "X-Correlation-Id"`).
- Contracts per feature folder (records; fields per the spec §5.3 and the requirement wording; every list item
  carries display names next to ids; every editable detail carries `string RowVersion` base64):
  `Auth/` LoginRequest, TokenResponse, SessionUser(StaffId, DisplayName, Role, IsDemo, CanManageStaff), RefreshRequest;
  `Donors/` DonorListItem, DonorDetail, CreateDonorRequest, UpdateDonorRequest (all `Optional<T>`), DonorQuery;
  `Patients/` same shape; `Appointments/` AppointmentListItem, AppointmentDetail, BookAppointmentRequest(DonorId,
  SlotStartUtc, OverrideReason?), RescheduleAppointmentRequest, SlotInfo(StartUtc, IsTaken), RecordDonationRequest(Units,
  Notes?), AppointmentQuery; `Requests/` RequestListItem, RequestDetail, CreateRequestRequest(PatientId, Units,
  RequiredBy), UpdateRequestRequest, RequestQuery; `Receipts/` ReceiptListItem, ReceiptDetail(… BagIds), ReceiptQuery;
  `Stock/` StockItem(BloodType, Available, ExpiringSoon, Threshold, BelowThreshold); `Dashboard/` DashboardResponse(Kpis,
  Months, Series, Demographics, LowStock), BloodTypeSeries(BloodType, int[] Donated, int[] Issued), GenderCount;
  `Staff/` StaffListItem(… HasPhoto, Salary?, Phone?), StaffDetail, CreateStaffRequest(… InitialPassword),
  UpdateStaffRequest, ResetPasswordResponse(TemporaryPassword); `Profile/` ProfileResponse, UpdateProfileRequest,
  ChangePasswordRequest(CurrentPassword, NewPassword); `Lookups/` RecordOption(Id, Title, Subtitle);
  `Health/` ReadinessResponse(Database: "ready" | "waking" | "resting").
- Routes (all under `/api/v1` except health): `auth/login`, `auth/demo`, `auth/refresh`, `auth/logout`;
  `donors`, `donors/{id}`, `donors/{id}/eligibility`; `patients`, `patients/{id}`; `appointments`,
  `appointments/{id}`, `appointments/slots?date=`, `appointments/{id}/donation`, `appointments/{id}/cancel`;
  `requests`, `requests/{id}`, `requests/{id}/fulfil`, `requests/{id}/cancel`; `receipts`, `receipts/{id}`; `stock`;
  `dashboard?from=&to=`; `staff`, `staff/{id}`, `staff/{id}/photo`, `staff/{id}/reset-password`; `me`, `me/password`,
  `me/photo`; `lookups/{donors|patients|staff|appointments|requests}?search=`; `demo/reset`; `/healthz`, `/readyz`.

- [ ] **Step 1: Spike.** Create the Desktop project referencing WPF-UI, LiveCharts2 and CommunityToolkit.Mvvm; a test
  in `Lifestream.Desktop.Tests` named `Spike_FluentWindowWithChart_RendersOffScreen` renders a `FluentWindow` content
  with one chart to a 800×600 PNG on an STA thread and asserts the PNG has more than 2 distinct colours. Run
  `vpk pack` on a self-contained publish once and record the output. If a library fails on `net11.0-windows`, apply the
  spec's fallback and record why.
- [ ] **Step 2: Write failing tests** `BloodType_DisplayRoundTrips_AllEight`, `Eligibility_Age17_NotEligible`,
  `Eligibility_89DaysSinceDonation_NotEligible_NextDateIsDay90`, `Eligibility_Age60_Eligible_Age61_NotEligible`,
  `Optional_AbsentNullAndValue_RoundTripThroughJson`, `Health_Healthz_Returns200_WithoutDatabase` (API with no
  connection string configured).
- [ ] **Step 3: Implement** Domain, Contracts and the API's `/healthz` until they pass.
- [ ] **Step 4: CI.** `ci.yml`: job `api` on `ubuntu-latest` with service `mcr.microsoft.com/mssql/server:2025-latest`
  (env `LIFESTREAM_TEST_SQL` set to it) running the API, Data, Domain, Client tests; job `desktop` on
  `windows-latest` running Desktop tests; both run the gate. Dependabot for nuget and github-actions weekly.
- [ ] **Step 5: Gate**, expected all green, 0 warnings. **Step 6: Commit** `chore: new .NET 11 solution skeleton`.

### Task 1: Database (module `db`)

**Ledger:** DB-01–DB-17, OWN-04, OWN-10, API-28 (data side), API-30 (schema side).

**Files:** `src/Lifestream.Data/` (`LifestreamDbContext.cs`, `Entities/*.cs`, `Configurations/*.cs`,
`Migrations/`, `Seed/DemoDataGenerator.cs`, `Seed/ScaleDataGenerator.cs`, `Seed/DemoReset.cs`),
`tests/Lifestream.Api.Tests/Data/` (fixture `SqlServerDatabaseFixture`), `docs/database.md` (ERD in Mermaid + how to add
a migration).

**Interfaces:**
- Consumes: Domain enums (stored as `nvarchar` with check constraints, via value converters).
- Produces: `LifestreamDbContext` with `DbSet`s `Donors, Patients, Appointments, Requests, Receipts, BloodBags,
  Staff, StaffPhotos, RefreshTokens, AuditLogs, DemoState`; `IDemoDataGenerator.Generate(DateOnly today) →
  DemoDataSet`; `DemoReset.RunIfDueAsync(LifestreamDbContext, DateOnly todayUtc, CancellationToken) → bool` and
  `DemoReset.ForceAsync(...)`; `ScaleDataGenerator.Generate(int factor)`; `SqlServerDatabaseFixture` (creates
  `Lifestream_Test_<guid>`, applies migrations, drops on dispose, asserts the drop).

- [ ] **Step 1: Failing tests** (each against a fixture database): `Migrations_ApplyToEmptyDatabase_NoPendingChanges`;
  `Staff_UsernameUniqueCaseInsensitive` (inserting "Admin" after "admin" throws); `Staff_PasswordHashColumn_Length256`;
  `Receipt_DonationWithoutAppointment_Rejected` and `Receipt_IssueWithRequest_Accepted`;
  `Receipt_SecondDonationForSameAppointment_Rejected`; `CheckConstraint_UnknownBloodType_Rejected`;
  `AllForeignKeyColumns_HaveIndexes` (query `sys.foreign_key_columns` vs `sys.index_columns`);
  `Database_HasNoTriggersOrUserFunctions`; `Timestamps_AreDatetime2`; `StaffQuery_DoesNotReadPhotoTable`
  (EF command interceptor sees no `StaffPhotos` in SQL); `DemoData_IsDeterministicForSameDate`;
  `DemoData_ContainsNoRealisticPhoneNumbers` (all phones in the reserved range `+1-555-01xx`);
  `DemoReset_RunsOncePerDay_UnderTenConcurrentCalls` (exactly one returns true);
  `DemoReset_RestoresSeedRowCountsAndChecksums_KeepsProtectedStaff`;
  `Donor_NameWithDiacriticsAnd60Chars_RoundTrips`; `Fixture_DropsDatabase` (after dispose, `DB_ID(name)` is null).
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement** entities, configurations (naming per spec §5.2,
  `rowversion` on editable entities, filtered unique index on `Receipt.AppointmentId`), initial migration
  `InitialSchema`, generators, `DemoReset` (`sp_getapplock` in a serializable transaction; set-based deletes and inserts
  via `ExecuteDeleteAsync` and batched `AddRange`).
- [ ] **Step 4: Gate.** Also run `dotnet ef migrations script --idempotent` and commit the output to
  `src/Lifestream.Data/Migrations/idempotent.sql` (regenerated by CI, which fails if it differs).
- [ ] **Step 5: Commit** `feat(db): schema, migrations, demo and scale data`.

### Task 2: Client shell and design system (module `client-shell`)

**Ledger:** SHELL-01–SHELL-44, OWN-05, OWN-07 (client side).

**Files:** `src/Lifestream.Client/` (`Api/LifestreamApiClient.cs`, `Api/WakeAwareHandler.cs`, `Session/SessionService.cs`,
`Navigation/NavigationService.cs`, `Shell/ShellViewModel.cs`, `Auth/LoginViewModel.cs`, `Common/ViewModelBase.cs`,
`Common/LoadState.cs`, `Common/Formatting.cs`, `Common/ToastService.cs`, `Common/DialogService.cs`),
`src/Lifestream.Desktop/` (`App.xaml(.cs)` with host and global exception handlers, `Theme/Tokens.xaml`,
`Theme/Typography.xaml`, `Controls/` Button styles, `Skeleton.xaml`, `RecordPicker`, `DataGridStyles`, `StatusChip`,
`EmptyState`, `ErrorState`, `Toast`, `ConfirmDialog`, `PasswordField`, `Shell/MainWindow.xaml`,
`Auth/LoginWindow.xaml`), `docs/design-system.md`, tests in `Lifestream.Client.Tests` and `Lifestream.Desktop.Tests`.

**Interfaces:**
- Consumes: Contracts (Task 0).
- Produces: `abstract partial class ViewModelBase : ObservableObject` with `LoadState State` (`Loading`, `Ready`,
  `Empty`, `Error`, `Waking`), `bool IsBusy`, `string? ErrorMessage`, `Task LoadAsync(CancellationToken)` template;
  `ILifestreamApiClient` with one typed method per route (`Task<PagedResult<DonorListItem>> GetDonorsAsync(DonorQuery,
  CancellationToken)` … ) returning `ApiResult<T>` (`Ok`, `Problem(code, title, correlationId)`);
  `INavigationService.NavigateTo<TViewModel>(object? parameter = null)`; `IToastService.Show(ToastKind, string message,
  string? actionLabel = null, Func<Task>? action = null)`; `IDialogService.ConfirmAsync(string title, string body,
  string dangerVerb) → Task<bool>`; `Formatting.Date(DateOnly)`, `Formatting.DateTime(DateTime utc)`; XAML styles keyed
  `Button.Primary|Secondary|Ghost|Danger|Icon` with attached property `ButtonAssist.IsBusy` and `ButtonAssist.BusyText`;
  `<controls:Skeleton Kind="List|Detail|Dashboard|StockTiles|Picker|Profile"/>`; `<controls:RecordPicker Kind="Donor|…"
  SelectedId="{Binding …}"/>`.

- [ ] **Step 1: Failing tests:** `LoginViewModel_StartsEmpty_SignInDisabled`; `LoginViewModel_NetworkError_ShowsInline_ReEnables`;
  `ApiClient_RetriesWakingResponse_WithSameIdempotencyKey`; `ApiClient_GivesUpAfter120s_WithWakingState`;
  `ApiClient_SharesOneHttpClient`; `Navigation_CreatesOneViewModelPerVisit_AndOneLoadCall`;
  `ViewModelBase_LoadNeverRunsOnDispatcherThread`; `GlobalHandler_UnobservedTaskException_ShowsDialog_NoCrash`;
  `Shell_HidesStaffForNonAdmin`; `Shell_OnDemoResetHeader_ShowsToastAndRefreshesCurrentScreen`;
  `Formatting_Date_Is05Mar2022`; off-screen render tests that save captures to `tests/captures/`:
  `PrimaryButton_SixStates_RenderDistinct` (idle/hover/pressed/focus/busy/disabled differ pixel-wise, busy keeps width),
  `Skeleton_ListKind_MatchesGridColumnWidths`, `Skeleton_ReducedMotion_IsStatic`, `LoginWindow_Renders`,
  `Shell_At1024x640_NoClipping`; `Theme_NoHexLiteralsInViews` (scans `*.xaml` outside `Theme/`);
  `Tokens_TextPairsMeetContrast` (computes ratios from `Tokens.xaml`, ≥ 4.5 for text, ≥ 3 for non-text);
  `AllInteractiveControls_HaveAutomationNames` (walks the logical tree of every view);
  `DataGrid_10000Items_CreatesUnder300Rows`.
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement** against a fake `ILifestreamApiClient`
  (`tests/Lifestream.Client.Tests/Fakes/FakeApiClient.cs`) so this task never needs the real API.
- [ ] **Step 4: Gate.** **Step 5: Commit** `feat(client): shell, design system, login, API client`.

### Task 3: Infrastructure as code and workflows (module `infra`; templates only, nothing applied)

**Ledger:** OWN-02, OWN-03, OWN-09 (workflow), INFRA-01, INFRA-04–INFRA-10, INFRA-12, INFRA-13, INFRA-17, INFRA-19.

**Files:** `infra/main.bicep`, `infra/main.bicepparam`, `infra/bootstrap-sql.ps1` (sets the Entra admin group, creates
contained users `lifestream-api` with `db_datareader`, `db_datawriter`, `EXECUTE` and `lifestream-migrator` with
`db_ddladmin` + reader/writer, using an Entra access token from `az account get-access-token`), `infra/README.md`
(runbook), `.github/workflows/deploy-api.yml`, `.github/workflows/release.yml`, `.github/workflows/secret-scan.yml`,
`src/Lifestream.Api/Dockerfile`, `tools/scan-artifacts.ps1`, `packaging/store/` (MSIX manifest + `README.md` with the
Store submission steps for the owner).

**Interfaces:**
- Produces: Bicep outputs `apiUrl`, `apiPrincipalId`, `migratorPrincipalId`; resources (prefix `lifestream-`): VNet
  `10.40.0.0/16` with subnet `aca` `/23` (service endpoint `Microsoft.Sql`), Container Apps environment (consumption,
  VNet-integrated, `appLogsConfiguration` none), container app `lifestream-api` (system identity, ingress external HTTPS
  port 8080, min 0, max 1, 0.25/0.5, startup probe `/healthz`, secrets `jwt-signing-key`), job `lifestream-migrate`
  (manual trigger, user-assigned identity `lifestream-migrator`, secret `owner-admin-password`), SQL firewall: VNet rule
  only (removes `Allow anyone` and `AllowAllWindowsAzureIps`), SQL Entra-only auth (separate parameter, default false,
  flipped by the orchestrator last), `CanNotDelete` locks on server and database, consumption budget $1 with an alert at
  50% to the owner's email (parameter), short-term backup retention 14 days if the free offer accepts it.
- `tools/scan-artifacts.ps1 -Path <dir>` exits 1 when any file contains, in UTF-8 or UTF-16LE, `Password=`, `Pwd=`,
  `User ID=` with a value, `database.windows.net` followed by credentials, a JWT, or a private key header.
- `deploy-api.yml`: on push to `master` (paths `src/Lifestream.Api/**`, `src/Lifestream.Data/**`,
  `src/Lifestream.Contracts/**`, `src/Lifestream.Domain/**`, `infra/**`) and `workflow_dispatch`: gate → `docker
  buildx build --provenance=false --sbom=false --platform linux/amd64` → push `ghcr.io/maiznadeem/lifestream-api:<sha>`
  → scan image filesystem with `scan-artifacts.ps1` → `azure/login` (OIDC, `id-token: write`) → update and start the
  migrate job, wait for success → `az containerapp update --image` → curl `/healthz` until 200 (5 min) →
  `actions/delete-package-versions@v5` `package-type: container`, `min-versions-to-keep: 3`.
- `release.yml`: on tag `v*`: gate → `dotnet publish src/Lifestream.Desktop -c Release -r win-x64 --self-contained
  -p:PublishReadyToRun=true` → `scan-artifacts.ps1` on the publish folder → `vpk pack` → `vpk upload github` →
  `SHA256SUMS.txt` attached → MSIX for the Store built as an extra artifact.

- [ ] **Step 1: Failing checks:** `tests/infra/BicepTests.ps1` asserting, from `az bicep build` JSON: no firewall rule
  with `0.0.0.0`–`255.255.255.255`, no `AllowAllWindowsAzureIps`, `minReplicas` 0, `maxReplicas` 1, logs destination
  none, startup probe path `/healthz`; `tests/infra/ScanArtifacts.Tests.ps1` (Pester) proving the scanner fails on a
  planted UTF-16LE `Password=` file and passes on a clean folder; `actionlint` passes on every workflow.
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement.** **Step 4:** `az deployment group what-if` against resource
  group `Lifestream` (read-only), output saved to the builder's record. **Step 5: Commit** `feat(infra): bicep,
  bootstrap and workflows`.

### Task 4: API (module `api`)

**Ledger:** API-01–API-45, DB-12 and DB-13 (query side), INFRA-06 (connection), INFRA-07 (app audit), OWN-06.

**Files:** `src/Lifestream.Api/` (`Program.cs`, `Auth/`, `Features/<Feature>/<Feature>Endpoints.cs` +
`<Feature>Queries.cs` + `<Feature>Validators.cs` per feature, `Infrastructure/ProblemDetailsSetup.cs`,
`Infrastructure/RateLimits.cs`, `Infrastructure/Idempotency.cs`, `Infrastructure/DemoResetMiddleware.cs`,
`Infrastructure/DatabaseWakeRetry.cs`, `Infrastructure/AuditInterceptor.cs`), `tests/Lifestream.Api.Tests/` (one file per
feature + `AuthorizationMatrixTests.cs` + `PerformanceSmokeTests.cs`).

**Interfaces:**
- Consumes: Contracts and routes (Task 0), `LifestreamDbContext`, generators and `DemoReset` (Task 1).
- Produces: the HTTP API exactly as `ApiRoutes`; configuration keys `ConnectionStrings:Lifestream` (local: Windows
  auth; Azure: `Authentication=Active Directory Managed Identity`), `Jwt:SigningKey`, `Jwt:Issuer` `lifestream-api`,
  `Demo:Enabled`, `Demo:WriteCapPerIpPerDay` 200, `Clinic:Hours` `09:00-17:00`, `Clinic:SlotMinutes` 30,
  `Clinic:TimeZone` `UTC` (default; the client always displays local time), `Stock:Threshold` 10 per blood type.

- [ ] **Step 1: Failing tests** (WebApplicationFactory + `SqlServerDatabaseFixture`), including at least:
  `Login_UnknownUserAndWrongPassword_SameResponse`; `Login_FiveFailures_LocksOut_429WithRetryAfter`;
  `Token_Expires15Min_RefreshRotates_DemoSessionsIndependent`; `AuthorizationMatrix_EveryRoute_EveryRole`
  (anonymous 401, Staff on admin routes 403, demo on staff writes 403 `demo_forbidden`); `NoResponse_ContainsPasswordHash`
  (serialises every endpoint's response and searches for the hash prefix); `Patch_OnlySuppliedFields_OthersUnchanged`;
  `Patch_WithStaleRowVersion_Returns409Concurrency`; `Post_WithRepeatedIdempotencyKey_ReturnsOriginalResult`;
  `Book_PastSlot_400`, `Book_TakenSlot_409SlotTaken`, `Book_Ineligible_WithoutReason_409`;
  `RecordDonation_CompletesExactlyThatAppointment_CreatesNBags_SecondTime409`;
  `Fulfil_IssuesOldestUnexpiredFirst_InsufficientStock409NoChange`; `Lists_DefaultPage50_Cap200_TotalCount`;
  `RequestsList_FilterByPatientName_OneSqlCommand` (command interceptor counts 1 data query + 1 count);
  `Search_IsAccentAndCaseInsensitive`; `Dashboard_DenseMatrix_ZerosIncluded`; `Dashboard_GenderCounts_EqualIndependentCount`
  (17 male donors on a fixture with 17); `Dashboard_RangeEndingToday_IncludesRecordAt2330Local`;
  `Stock_ExcludesExpired_FlagsBelowThreshold`; `StaffList_110Staff_Under20KB_NoImageBytes`;
  `PhotoUpload_Reencodes256Under30KB_Rejects1MBAndWrongType`; `ChangePassword_RequiresCurrent_Min12`;
  `DeleteStaff_SelfOrLastAdmin_409`; `Errors_AreProblemDetails_WithCorrelationId_NoExceptionText`;
  `Healthz_NeverOpensConnection`; `Readyz_ReportsWaking_On40613`; `Request_AfterReset_CarriesDemoResetHeader`;
  `DemoResetEndpoint_CooldownTenMinutes`; `DemoWrites_CappedPerIp`; `Logs_ContainNoPasswordsOrTokens`
  (captures log output during login); `Perf_ListEndpoints_P95Under100ms_At100x` and
  `Perf_Dashboard_P95Under100ms_At100x` (tagged `Category=Perf`, run locally, reported in CI).
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement** endpoint by endpoint, committing per feature.
- [ ] **Step 4: Gate** plus `dotnet test --filter Category=Perf`; results saved to `docs/perf/api-results.md`.
- [ ] **Step 5: Commit** `feat(api): endpoints, auth, demo reset`.

### Task 5: Client features part 1: Dashboard, Donors, Patients, Stock (module `client-features`)

**Ledger:** FEAT-01–FEAT-08, FEAT-19, FEAT-27–FEAT-31 (and SHELL-24, SHELL-37 usage on these screens).

**Files:** `src/Lifestream.Client/Features/{Dashboard,Donors,Patients,Stock}/*ViewModel.cs`,
`src/Lifestream.Desktop/Features/{Dashboard,Donors,Patients,Stock}/*View.xaml`, tests in both test projects.

**Interfaces:** Consumes Task 0 contracts and Task 2's `ViewModelBase`, `ILifestreamApiClient`, `INavigationService`,
`IDialogService`, `IToastService`, controls and styles. Produces `DonorsViewModel`, `DonorEditorViewModel`,
`PatientsViewModel`, `PatientEditorViewModel`, `DashboardViewModel`, `StockViewModel`.

- [ ] **Step 1: Failing tests:** `DonorsList_TypingSearch_Debounced300ms_OneApiCall`; `DonorsList_PlaceholderNeverSent`;
  `DonorsList_ShowsSkeletonThenRows_EmptyState_ErrorWithRetry`; `DonorEdit_PrefilledAndSendsOnlyChangedFields`;
  `DonorEdit_CanClearOptionalField` (sends explicit null); `DonorEdit_On409_ShowsReloadPrompt`;
  `DonorDelete_ConfirmsNamingRecord_RemovesOnlyAfterApiOk`; `DonorForm_DobInFuture_InlineError_FocusMoves`;
  `Dashboard_OneApiCall_SkeletonWithin100ms`; `Dashboard_KpiCard_OpensFilteredList`;
  `Stock_TileBelowThreshold_HasIconTextAndToken`; render captures `DonorsView_Loaded`, `DonorsView_Skeleton`,
  `DonorsView_Empty`, `DashboardView_Loaded`, `StockView_Loaded` saved to `tests/captures/`.
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement.** **Step 4: Gate.** **Step 5: Commit**
  `feat(client): dashboard, donors, patients, stock`.

### Task 6: Client features part 2: Appointments, Requests, Receipts, Staff, Profile (module `client-features`)

**Ledger:** FEAT-09–FEAT-18, FEAT-20–FEAT-26 (and FEAT-01–06 on these screens).

**Files:** `src/Lifestream.Client/Features/{Appointments,Requests,Receipts,Staff,Profile}/`,
`src/Lifestream.Desktop/Features/{Appointments,Requests,Receipts,Staff,Profile}/`, tests.

**Interfaces:** as Task 5, plus `RecordPicker` from Task 2. Produces the five feature view models and views, and
`ReceiptPrintView` (FlowDocument; Print via `PrintDialog`, Save as PDF via the Microsoft Print to PDF printer).

- [ ] **Step 1: Failing tests:** `BookAppointment_PastDatesBlocked_TakenSlotsDisabled`; `SlotPicker_ShowsLocalTimes`;
  `SlotPicker_Parses1200AMand1200PMCorrectly`; `Reschedule_DateOnly_KeepsTime`;
  `RecordDonation_FromAppointment_PrefillsDonor_ShowsEligibilityBanner_OverrideNeedsReason`;
  `FulfilRequest_InsufficientStock_ShowsMessage_NoChange`; `AppointmentsList_FilterByDonor_UsesDonorId`;
  `Receipts_SearchAndNamesNotBareIds`; `Receipt_PrintViewRenders`; `StaffList_NoPasswordColumn_LazyThumbnails`;
  `StaffEditor_NewStartsEmptyDobAndSalary_PasswordMaskedWithConfirm`; `StaffDelete_OwnRecordDisabledWithTooltip`;
  `ResetPassword_ShowsTemporaryOnce`; `Profile_SaveEnabledOnlyWhenChanged`;
  `ChangePassword_Dialog_WorksWithAndWithoutPhoto`; `Profile_SendsPhotoOnlyWhenReplaced`; captures for each view.
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement.** **Step 4: Gate.** **Step 5: Commit**
  `feat(client): appointments, requests, receipts, staff, profile`.

### Task 7: Performance and end-to-end pass

**Ledger:** OWN-07, SHELL-09, SHELL-13, SHELL-38, FEAT-27 (timings), DB-06, API-21–API-26 (timings), INFRA-01.

**Files:** `tests/perf/run-perf.ps1` (starts the API locally against a 100x fixture database, runs a scripted client
session, measures app launch to login window via `Process.Start` + UIA window-appeared event in a hidden desktop-less
test host, writes `docs/perf/results.md`), `tests/Lifestream.Desktop.Tests/EndToEnd/` (client view models against the
real local API).

- [ ] **Step 1:** Failing check `run-perf.ps1 -AssertBudgets` with the spec §5.5 table as thresholds.
- [ ] **Step 2:** Fix what fails (within the owning module's files; report cross-module needs to the orchestrator).
- [ ] **Step 3:** `tools/scan-artifacts.ps1` on a full local publish of Desktop and on `docker save` of the API image:
  expect exit 0. **Step 4: Gate.** **Step 5: Commit** `perf: budgets and end-to-end checks`.

### Task 8: Deploy, release, clean up (orchestrator only)

**Ledger:** INFRA-02, INFRA-03, INFRA-04, INFRA-05, INFRA-11 (owner decision recorded), OWN-02, OWN-03, OWN-09.

- [ ] Export the current Azure `BloodBank` to `G:/Claude/workspace/lifestream/archive/2026-11-19/BloodBank-pre-2.0.bacpac`
  (never committed). Create the GitHub OIDC app registration with a federated credential for `master`, role
  Contributor on resource group `Lifestream` only. Apply `main.bicep`; run `bootstrap-sql.ps1`; set the two Container
  Apps secrets from the local secret store; run the migrate job (drops old tables, creates the new schema, seeds demo
  data and the owner admin); push `overhaul`, open the draft PR, let CI pass, merge to `master` (owner pre-approved),
  let `deploy-api.yml` deploy; end-to-end smoke against the live API (login, demo, each list, one write, reset);
  flip Entra-only auth; prove the old admin password and every credential found in git history now fail.
- [ ] Tag `v2.0.0`, let `release.yml` publish; download the release asset into a clean folder and scan it; delete
  the assets of `Lifestream-1.0.0` and `Lifestream-1.0.1` and replace their notes with a pointer to 2.0.0.
- [ ] Close: remove worktrees, delete scratch folders holding bacpac data, confirm no `Lifestream*` database exists
  locally, stop the monitor, delete the watchdog task, final report.
