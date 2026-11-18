# Database

Azure SQL (SQL Server) behind the API, reached only through EF Core in `src/Lifestream.Data`. The schema exists only as
versioned EF Core migrations applied to an empty database; there is no hand-run SQL, no trigger, no function and no stored
procedure.

## Entity-relationship diagram

```mermaid
erDiagram
    Donor ||--o{ Appointment : books
    Patient ||--o{ Request : needs
    Appointment ||--o| Receipt : "donation receipt (once)"
    Request ||--o{ Receipt : "issue receipts"
    Staff ||--o{ Receipt : records
    Receipt ||--o{ BloodBag : "donation creates"
    Receipt ||--o{ BloodBag : "issue consumes"
    Staff ||--o| StaffPhoto : has
    Staff ||--o{ RefreshToken : owns
    Staff {
        int Id PK
        varchar Username UK "case-insensitive"
        varchar PasswordHash "256, salted PBKDF2 only"
        bool IsProtected
        bool IsDemo
        bool HasPhoto
    }
    StaffPhoto {
        int StaffId PK
        varbinary Content "30 KB max"
        int Width "256 max"
    }
    Receipt {
        int Id PK
        varchar Kind "Donation or Issue"
        int StaffId FK
        int AppointmentId FK "donation only, unique"
        int RequestId FK "issue only"
    }
    BloodBag {
        int Id PK
        varchar BloodType
        date ExpiresOn
        varchar Status
        int DonationReceiptId FK
        int IssueReceiptId FK "only when Issued"
    }
    AuditLog {
        int Id PK
        int StaffId "not a foreign key"
        varchar Action
        varchar Entity
        int EntityId
        datetime2 OccurredAtUtc
    }
    DemoState {
        int Id PK "always 1"
        date LastResetDate
    }
```

## Conventions

- Tables are singular PascalCase (`Donor`, `BloodBag`); columns are PascalCase; no underscores.
- Every table has an `int` identity `Id` (`StaffPhoto` is keyed by its `StaffId`). Foreign keys are `int`, named `<Thing>Id`,
  and every one has an index that starts with it.
- Enums (blood type, gender, designation, shift, role, every status, receipt kind) are stored as their names in `varchar`
  and guarded by a `CHECK` constraint built from the enum, so an unknown value is rejected by the database.
- Timestamps are `datetime2(3)` in UTC, columns end in `Utc`, and defaults use `SYSUTCDATETIME()` (the server clock, never a
  client clock). Date-only values (`DateOfBirth`, `LastDonated`, `RequiredBy`, `ExpiresOn`, `LastResetDate`) are `date`.
- Editable rows (`Donor`, `Patient`, `Staff`, `Appointment`, `Request`) carry a `rowversion` for optimistic concurrency.
- `Staff.Username` is unique with the case-insensitive collation pinned on the column. `Donor.Name`, `Patient.Name` and
  `Staff.DisplayName` use `Latin1_General_100_CI_AI` so a search is case- and accent-insensitive without a function on the column.
- `Staff.PasswordHash` is `varchar(256)` with a `CHECK` that it is an ASP.NET Core Identity v3 hash
  (PBKDF2-HMAC-SHA512, base64, starts `AQAAAAI`). Plain text cannot be stored. `PasswordHashing` uses 600,000 iterations.
- Staff photos live in `StaffPhoto` (at most 30 KB, 256 px). `Staff.HasPhoto` tells the API a photo exists, so a Staff query
  never touches the photo table.
- `Receipt` is one table for both directions. A `CHECK` requires an appointment (and no request) for a `Donation` and a
  request (and no appointment) for an `Issue`; a filtered unique index on `AppointmentId` lets an appointment be receipted once.
- `BloodBag` holds one row per unit. `ExpiresOn` is collection date plus 42 days, defined once in `StockRules` (with the
  3-day "fresh" window). A bag is `Issued` only together with an `IssueReceiptId`.

## Decisions recorded here

- **Attendance** (the old app's table, with no screen in the old app) is **absent**: it is not in any migration and has no endpoint.
  `BloodBag` is kept. (Owner decision DB-17, default taken.)
- **Audit.** Every insert, update and delete on `Donor`, `Patient`, `Request`, `Receipt`, `Appointment` and `Staff` writes an
  `AuditLog` row (user id from `LifestreamDbContext.CurrentStaffId`, action, entity, entity id, server UTC time) from
  `SaveChanges`. Updates and deletes are in the same batch as the change. An insert whose id the database generates is audited by a
  second `SaveChanges` straight after the first, so wrap multi-step writes in one transaction (the API does) to make them atomic.
  Bulk system work (generators, the demo reset) turns auditing off with `AuditEnabled = false`.
- **No logic in the database.** Donation (receipt, bags, completing the appointment, `Donor.LastDonated`) and issue (oldest
  unexpired bags first) are API code inside transactions.
- **Ids after a demo reset are not restarted** (`DBCC CHECKIDENT` and `IDENTITY_INSERT` need `ALTER`, which the API identity
  does not have). Compare content, not ids.

## Data you can load

All data comes from code, never from a backup.

- `DemoDataGenerator.Generate(today)`: 11 staff (one is the public `demo` account), 40 donors, 35 patients, 300 appointments,
  150 requests, 270 receipts and their bags. Dates are relative to `today`. The same date always gives the same rows.
  Names come from two lists in `SyntheticVocabulary`, phones are in the fictional range `+1-555-0100` to `+1-555-0199`,
  addresses are on "Sample" streets in "Demoville". Every staff row holds the hash of a random password that was thrown away, so no
  seeded account has a known password; the demo login issues a token without one.
- `ScaleDataGenerator.Generate(100)`: exactly 100 times the above (27,000 receipts, 30,000 appointments, 15,000 requests),
  for performance tests. `ScaleDatabaseFixture` in the tests loads it into a throwaway database.
- `DemoReset.RunIfDueAsync` / `ForceAsync` rebuild every table from the generator, keeping protected staff (the owner admin).

## Add a migration

Install the EF tool once (the repository pins no global tool): `dotnet tool install dotnet-ef --tool-path <folder> --version "11.*"`.

1. Change the entities or `Configurations/`.
2. `dotnet-ef migrations add <Name> --project src/Lifestream.Data --output-dir Migrations`
3. Read the generated `Up`; EF never writes data fixes for you.
4. Regenerate the idempotent script and commit it with the migration:
   `dotnet-ef migrations script --idempotent --project src/Lifestream.Data --output src/Lifestream.Data/Migrations/idempotent.sql`
5. `dotnet test Lifestream.slnx -c Release`. `Migrations_ApplyToEmptyDatabase_NoPendingChanges` fails if the model and the
   migrations disagree, and `IdempotentScript_Committed_MatchesWhatTheMigrationsGenerate` fails if step 4 was forgotten.

Never edit an applied migration. Add a new one.

## Tests

`tests/Lifestream.Api.Tests/Data/` runs against a real SQL Server named by `LIFESTREAM_TEST_SQL` (default
`Server=localhost;Integrated Security=true;TrustServerCertificate=true`). Each test class gets a database called
`Lifestream_Test_<guid>`, created by the migrations and dropped by the fixture, which checks the drop.
