# Lifestream rewrite: consolidated requirements

Source: the four audits (SEC, PERF, CODE, UX) and research.md. 157 requirements plus 3 moot findings, covering 138 finding ids. Machine-readable copy: `requirements.json`.

Each requirement is one testable sentence. A source shown in parentheses means the requirement comes from the project background (or an audit owner decision) rather than a finding id. No credential values appear in this file.

## Database (db)

| Id | Sev | Requirement | Sources | Proof |
|---|---|---|---|---|
| DB-01 | low | The database schema must be created only by versioned migrations applied to an empty database, and a fresh-database migration run must end with zero drift against the model. | CODE-43 | CI job: migrate an empty SQL container, then run the EF drift check (no pending model changes). |
| DB-02 | critical | The Staff password column must hold only a salted password hash in a column of at least 256 characters, and a test must fail if any Staff row (including seed rows) holds a value that is not a valid hash. | SEC-04 SEC-09 | Integration test over the migrated and seeded database: every Staff.password parses as a valid hash, column max length >= 256. |
| DB-03 | high | Staff.username must be unique (case-insensitive), and inserting a duplicate username must be rejected by the database. | CODE-18 | Integration test: second insert of the same username (different case) throws a unique-violation. |
| DB-04 | high | Receipt must have foreign keys to Staff, Request and Appointment, and every foreign key column must have a supporting index. | PERF-12 | Schema test: query sys.foreign_keys and sys.index_columns; assert the three Receipt FKs exist and no FK column lacks an index. |
| DB-05 | high | Every column the API filters or sorts on (including Request status/date/blood type, BloodBag type/date, Receipt process, Staff.username) must be indexed, and no API query over a table with more than 5,000 rows may use a table scan on the 100x dataset. | PERF-12 | Run the full API query set against the seeded 100x database and assert no scan operator in any plan on tables over 5,000 rows. |
| DB-06 | high | A script in the repository must generate a deterministic 100x dataset (about 27,000 receipts) and the performance tests must run against it as well as the 1x demo size. **OWNER:** Performance budgets: test at 1x demo size only, 100x only, or both? Recommended default: both (1x for UI timing, 100x for query plans). | PERF-12 | CI performance job loads the 100x seed and reports row counts per table. |
| DB-07 | medium | The database must retain CHECK or enum-equivalent constraints for blood type, gender, shift, designation and every status, and an insert with a value outside the allowed list must be rejected. | SEC-14 CODE-28 | Integration test: one invalid value per constrained column is rejected. |
| DB-08 | medium | The schema must use one naming convention (no mixed Donor_ID/id/Appo_ID styles), integer or GUID ids of one type across tables and DTOs, date-only columns typed as date, and no deprecated image column type. | CODE-29 | Schema lint test over sys.columns for naming pattern and types; DTO test that all id properties are the same type. |
| DB-09 | medium | All stored timestamps must be UTC, written by the server (never client clocks), and date-only values must be stored as date. | CODE-30 | Test: insert via API with a client clock shifted by 5 hours; stored value equals server UTC time within 5 seconds. |
| DB-10 | medium | The database must contain no triggers, and donation or receiving logic must be implemented in the API inside transactions. | PERF-14 | Schema test: sys.triggers is empty for all application tables. |
| DB-11 | high | Staff photos must be stored outside the Staff row (separate table, at most 30 KB each, 256 px maximum) so that no Staff query reads image bytes. **OWNER:** Should staff photos stay in the database? Recommended default: yes, in a separate StaffPhoto table with 256 px thumbnails (no extra Azure resource, so zero extra cost); move to object storage only if size becomes a problem. | PERF-08 | Schema test: Staff has no binary column; photo table size check after uploading an 8192x4608 image. |
| DB-12 | high | The gender-count query must return counts equal to an independent count of each table, read each table once, and contain no join between Donor and Patient. | PERF-03 | Seed 17 male donors and 19 male patients and assert 17 and 19; assert plan has no join operator. |
| DB-13 | medium | Date-range queries (dashboard counts, chart data, receipts, requests, appointments) must use half-open ranges (>= start and < next day), so a record at 23:59 on the end date is counted. | PERF-13 | Integration test: receipt at 23:59 on the end date is counted in every range query. |
| DB-14 | medium | The freshness and expiry windows (3-day and 42-day) must be defined in one configuration value, and the stock-by-blood-type query must read under 40 pages on the 100x dataset. | PERF-13 | Config test: windows appear once; SET STATISTICS IO on the stock query at 100x shows < 40 logical reads. |
| DB-15 | high | Seed and demo data must be generated by a script in the repository, must contain only clearly synthetic people, and must hold only hashed demo passwords (never a backup of real data). **OWNER:** Is any row or photo in the old BloodBank.bacpac a real person, and did those logins ever work in production? Recommended default: treat all of it as unusable, discard it, and generate fresh synthetic data. | SEC-06 | CI test: seed script output contains no row outside the synthetic name list; password column check (see DB hash requirement); repo contains no .bacpac. |
| DB-16 | high | Every insert, update and delete on donor, patient, request, receipt, appointment and staff rows must write an audit record containing user id, action, entity, entity id and UTC time. | SEC-08 | Integration test: perform each write as a user and assert exactly one matching audit row exists. |
| DB-17 | low | The schema must keep BloodBag (needed for stock and expiry), and the owner decision on the Attendance table (no UI in the old app) must be recorded as either present with an endpoint or absent from migrations. **OWNER:** Keep the Attendance table (no UI in the old app, 83 unused rows)? Recommended default: drop it, keep BloodBag. | CODE-52 | Migration test: Attendance exists only if an API endpoint uses it. |

## API (api)

| Id | Sev | Requirement | Sources | Proof |
|---|---|---|---|---|
| API-01 | critical | The login endpoint must verify the password against the stored hash and must return the same generic invalid-credentials response and status for an unknown user and a wrong password. | SEC-04 SEC-10 | API test: both cases return 401 with identical body shape; no hash or password in any response. |
| API-02 | medium | The API must issue access tokens that expire in 15 minutes or less, support refresh, and reject expired or server-side revoked tokens with 401. | SEC-10 | API test: advance clock 16 minutes, call endpoint, expect 401; revoke refresh token, expect refresh to fail. |
| API-03 | medium | The login endpoint must lock out a user after 5 failed attempts and rate-limit by IP, returning 429 (with Retry-After) until the lock expires. | SEC-10 | API test: 6 failures for one user and for one IP produce 429; correct password during lock is also refused. |
| API-04 | high | The demo login endpoint must have its own per-IP rate limit and request-size limit, separate from the normal login limit. | SEC-10 SEC-07 | API test: exceed the demo limit from one IP and assert 429 while normal login still works. |
| API-05 | high | The demo account must have a low-privilege role that cannot create, edit, delete or reset staff accounts, change passwords, or reach any data outside the demo dataset. **OWNER:** What may the demo user do? Recommended default: ordinary staff role (read and write sample donors, patients, appointments, requests, receipts), no staff management, no password change. | SEC-07 | API test: demo token gets 403 on every staff-management and password endpoint. |
| API-06 | high | Every endpoint must enforce its role server-side, returning 401 to an anonymous caller and 403 to a caller of the wrong role, proven by a test matrix of every endpoint against every role plus anonymous. | SEC-05 CODE-17 | Generated API test matrix (endpoint x role) asserting expected status codes. |
| API-07 | high | Staff-management endpoints must be Admin-only, and a non-admin staff member must be able to read and edit only their own profile. | SEC-05 | API test: non-admin gets 403 on staff create/update/delete/list and on another user's profile; own profile 200. |
| API-08 | medium | Receipts and audit records must take the staff id from the authenticated token, and a staff id supplied in a request body must be ignored or rejected. | SEC-14 | API test: post a receipt with a different staffId in the body; stored staff id equals the token user. |
| API-09 | high | The API must validate every request DTO (lengths, ranges, enums, DOB in the past, donation quantity within a defined range, no DateTime.MinValue or null dropdown values) and reject invalid input with 400, with one automated test per rule. | SEC-14 CODE-14 UX-33 | Parameterised API tests, one per rule, asserting 400 and field-level problem details. |
| API-10 | critical | Booking a donor appointment must reject past dates and times outside configured clinic hours or already taken, returning 400 or 409 rather than failing. | UX-32 CODE-14 | API test: past date, off-hours time and a taken slot are each rejected. |
| API-11 | low | Eligibility must be computed server-side (donor at least 18, last donation more than 90 days ago, medical flag) and an ineligible donor must be bookable only with an explicit override reason that is stored. **OWNER:** Are the documented clinical eligibility rules (age 18+, 3 months since last donation, medical issue flag) the ones to enforce? Recommended default: yes, with an override reason. | UX-38 | API test: ineligible donor without reason gets 409; with reason succeeds and reason is stored. |
| API-12 | high | Every error response must be RFC 7807 problem details with a stable error code and a correlation id, and must never contain exception text, SQL text, or server, table or constraint names. | SEC-13 CODE-11 | API test: force a constraint violation and a thrown exception; assert generic body, error code and correlation id header, and no SQL substrings. |
| API-13 | high | Update and delete of an id that does not exist must return 404 for every entity. | CODE-13 | API test per entity: PUT and DELETE a missing id return 404. |
| API-14 | medium | Create and update endpoints must return the saved resource so that no list reload is needed after a save. | PERF-11 | API test: POST returns 201 with the full saved row including its id. |
| API-15 | critical | A partial update must change only the supplied fields, leave every other column byte-identical (including salary, date of birth and photo), and allow an optional field to be cleared explicitly. | CODE-03 UX-30 | API test: PATCH one field, read the row before and after, assert only that field differs; explicit null clears an optional field. |
| API-16 | critical | Issuing a donation receipt must, in one transaction, complete exactly the receipted appointment, reject a second receipt for it, and leave other appointments untouched even when donor ids and appointment ids differ. | CODE-02 | API test: donor 12 and appointment 5; receipt appointment 5; only 5 becomes Completed; second receipt returns 409. |
| API-17 | high | Appointment queries for a donor must match on the appointment's donor id and return only that donor's appointments, even when donor ids and appointment ids differ. | CODE-04 | API test: seed donor id 12 with appointment id 5; filter by donor 12 returns appointment 5 only. |
| API-18 | critical | Receiving blood must consume the oldest non-expired bags first inside one transaction, and a request exceeding available stock must be rejected with no bag changed. | CODE-02 PERF-14 | API test: three bags of different ages; receive 2; the two oldest fresh bags are consumed; over-request returns 409 and stock is unchanged. |
| API-19 | medium | A donation of N units must create N bags with one set-based insert statement, and a bulk insert of 1,000 appointments must succeed. | PERF-14 | API/SQL test: a 50-unit donation issues one INSERT statement (profiler count); bulk 1,000 appointments returns success. |
| API-20 | medium | Stock and expiry results must exclude expired bags by date at query time, with no scheduled timer or keep-alive that wakes the database. | PERF-14 PERF-13 | API test: an expired bag is not counted without any job having run; infra check shows no timer trigger for the API. |
| API-21 | high | Every list endpoint must return one page of at most 50 rows by default with a total count, server-side filter and sort, and no list endpoint may ever return more than 200 rows. | PERF-06 | API test on 100x data: every list endpoint's largest response is 200 rows or fewer, first page under 500 ms. |
| API-22 | high | A filtered request, receipt or appointment list must be served by one query that joins the referenced patient, donor and staff names, so a filter click makes exactly one database call regardless of row count. | PERF-05 CODE-26 | API test with 10,000 requests: filter issues one database command; p95 under 150 ms. |
| API-23 | high | Text search on donors, patients and requests must match name, id and contact, and must use an index-friendly (prefix or full-text) query rather than LIKE '%x%'. | UX-18 PERF-06 | API test: search by id, by contact and by name prefix returns the record; plan shows seek on 100x data. |
| API-24 | high | List responses for appointments, requests and receipts must include the display names of the referenced donor, patient and staff member next to their ids, and must not include constant placeholder values (such as an appointment 'Name'). | UX-19 | API contract test: each listed DTO has donorName/patientName/staffName populated. |
| API-25 | low | Date filters on receipts, requests and appointments must include every record on the 'To' date, whatever its time. | CODE-51 | API test: receipt at 15:00 is returned when To equals that date. |
| API-26 | high | The dashboard must be served by one endpoint returning all blocks for a rolling date range (default last 12 months), computed with at most one scan per table, cached for at most 60 seconds, and answering in under 100 ms on the 100x dataset. | PERF-04 CODE-09 | API test at 100x: single request, one Receipt scan in the plan, response time under 100 ms; range changes with the current date. |
| API-27 | high | The dashboard chart data must be a dense matrix (every blood type for every monthly bucket, zeros included) with equal-length series. | CODE-36 UX-13 | API test: skip a blood type in a month; response still has an entry with zero; all series have equal length. |
| API-28 | medium | Demographic counts must include every gender value stored, including Other. | UX-14 | API test: seed one Other donor; the response includes Other with count 1. |
| API-29 | high | The stock endpoint must return, per blood type, units available, units expiring within 7 days and a below-threshold flag from a configured minimum level. | UX-25 UX-15 | API test: seed bags near expiry and below threshold; assert the three fields. |
| API-30 | critical | No API response may contain a password or password hash, and no endpoint may use SELECT * on a table holding credentials. | SEC-04 CODE-39 SEC-18 | API contract test: scan every response schema for password/hash members; source scan finds no SELECT * on Staff. |
| API-31 | high | Staff list and login responses must not include image bytes, the staff list for 110 staff must be under 20 KB, and a photo must be served on demand as a thumbnail of at most 256 px and 30 KB. | PERF-08 PERF-15 | API test: list response size < 20 KB for 110 staff; photo endpoint returns <= 30 KB. |
| API-32 | high | Photo upload must decode and re-encode the image server-side to at most 256x256 JPEG under 30 KB, reject uploads over 1 MB or with a wrong content type, and never serve executable content types. | SEC-19 PERF-09 | API test: an upload over 1 MB is rejected; a 900 KB valid image is stored under 30 KB; a renamed .exe is rejected. |
| API-33 | medium | API responses must include only the fields each role needs (salary and staff contact only for Admin). | SEC-15 | API test: non-admin staff DTO has no salary or contact fields. |
| API-34 | critical | Changing a password must require the current password, enforce a minimum length of 12, store a hash, and an admin 'reset password' must set a new temporary value without ever returning any password. | SEC-09 UX-27 UX-39 | API test: wrong current password is rejected; 11-character password rejected; admin reset response has no password. |
| API-35 | medium | The API must refuse to delete a staff member who is the caller or the last admin, and deleting a staff member with dependent rows must return a clean 409 with an error code. | CODE-33 | API test: self-delete and last-admin delete return 409; delete with attendance returns 409 problem details. |
| API-36 | medium | All data access must use parameterised or typed queries only, and security-relevant writes (profile, password, staff) must surface failure as an error response and never as a silent false. | SEC-14 SEC-18 CODE-31 | Source scan: no string-concatenated SQL; test: a failing password write returns a 4xx/5xx problem details, not 200. |
| API-37 | medium | The health endpoint must answer without opening a database connection, and a separate readiness result must report 'database waking' as a distinct state. | PERF-10 CODE-38 | API test with the database stopped: /healthz returns 200; readiness returns waking. |
| API-38 | high | When the database is resuming (transient error 40613 or connect timeout), the API must retry for up to about two minutes and then return 503 with Retry-After and the error code database_waking, never a 500 with exception text. | PERF-10 CODE-38 UX-10 SEC-13 | API test with a fake SQL provider failing 40613 for N seconds: request succeeds after resume; permanent failure returns 503 database_waking. |
| API-39 | high | The first request of each UTC day must trigger a reset of the demo dataset from the repository seed script, exactly once even under concurrent requests, with no timer or scheduled job. | SEC-06 PERF-14 | API test: two simultaneous first-of-day requests cause one reset; infra check shows no scheduler; seeded counts match the seed script. |
| API-40 | high | After a demo reset, every table must hold exactly the seed data (row counts and checksums equal), and visitor-made changes and uploaded photos must be gone. | SEC-06 SEC-07 | API test: modify and add records, force reset, compare table checksums to the seed. |
| API-41 | critical | The reset operation must run under an identity that can reach only the demo database. | SEC-07 SEC-02 | Infra test: the reset identity has no role on any non-demo database. |
| API-42 | medium | The API must apply a per-IP rate limit to all endpoints and a request body size limit, returning 429 or 413, and must cap replica count to bound cost. | SEC-10 | API test: exceed the limit and assert 429; oversize body returns 413; replica max in IaC <= 2. |
| API-43 | high | Application logs must be structured, contain no passwords, tokens or personal data, and carry user id and correlation id. | SEC-08 SEC-13 | API test: log capture during login and donor create contains no password or donor name; contains correlation id. |
| API-44 | low | Updating an appointment's date must not change its time unless a new time is supplied. | CODE-50 | API test: edit date only on an 11:00 appointment; time remains 11:00. |
| API-45 | medium | Blood types, genders, shifts, designations and statuses must be defined once in the shared contracts and exposed to the client, and the client's dropdown lists must equal the server's lists. | CODE-28 | Test: compare client list sources with the /lookups response and DB constraint values. |

## Client shell (client-shell)

| Id | Sev | Requirement | Sources | Proof |
|---|---|---|---|---|
| SHELL-01 | critical | The client must install a global exception handler for dispatcher, AppDomain and task exceptions that logs the error and shows a friendly dialog, and no unhandled exception raised by any command may terminate the process. | CODE-10 CODE-01 | UI test: throw inside a command and a Task; the process stays alive and the dialog appears; no Console.WriteLine used for errors. |
| SHELL-02 | critical | A login attempt that fails with a network or server error must show an inline message, re-enable the button and never terminate the process, with distinct messages for wrong credentials, server unreachable and server waking (the last two with Retry). | CODE-01 UX-10 | View-model test against a stub that throws each failure; assert message text, button enabled, process alive. |
| SHELL-03 | high | The login form must start empty with the button disabled until both fields have values, must contain no prefilled or printed credentials, and the password box must always display its bound state. | CODE-16 SEC-07 UX-09 | View-model test: new LoginViewModel CanExecute is false; XAML scan finds no credential text; password box two-way test. |
| SHELL-04 | high | The login screen must offer an explicit 'Try the demo' button that signs into the demo account. **OWNER:** How is the public demo login presented? Recommended default: a 'Try the demo' button on the login screen that signs into the sample-data account, with no credentials printed. | UX-09 SEC-07 | UI capture of the login screen shows the button; click test signs in with demo token. |
| SHELL-05 | medium | The sign-in button must show idle, hover, pressed, keyboard-focus, busy ('Signing in...' with a spinner and read-only fields, within 100 ms of the click) and disabled states, and Enter must submit via IsDefault. | UX-11 PERF-10 | Off-screen UI capture of each state; test that busy appears within 100 ms of invoking the command. |
| SHELL-06 | medium | The login screen must show a 'Forgot password' control only if it works, and must never throw NotImplementedException. | UX-11 SEC-10 | UI test: control absent or completes a flow; grep finds no NotImplementedException. |
| SHELL-07 | high | The API client must, on timeout or a database-waking response, retry with backoff and cancellation for up to about 120 seconds, show a 'waking up the server' state after 3 seconds, and let the user cancel or retry at any time. | PERF-10 CODE-38 UX-10 | Client test with a fake handler returning 503 then 200 after N seconds; assert state transitions and cancellation. |
| SHELL-08 | medium | The API client must reuse one HTTP/2 connection for all calls instead of opening a new connection per call. | PERF-10 | Test: 20 sequential calls against a local server produce one connection. |
| SHELL-09 | critical | No API call may execute on the UI thread, and the dispatcher must process input with no gap over 50 ms while a screen loads over a throttled network. | PERF-01 CODE-08 | Dispatcher-latency probe test under throttled stub; analyzer forbids synchronous waits on UI thread. |
| SHELL-10 | medium | The client must have one AsyncRelayCommand with IsRunning, exception capture and CanExecute false while running, replacing the two duplicate command classes. | CODE-21 | Unit test: command exposes IsRunning, rejects re-entry, captures exception; repository has one command class. |
| SHELL-11 | medium | Every view model must expose bindable IsLoading and IsBusy state. | CODE-19 | Reflection test over all view models; behaviour test observes IsLoading during load. |
| SHELL-12 | medium | All bound view-model members must be properties with change notification, and no view model may have a public mutable field. | CODE-20 | Reflection test: no public mutable fields on view models; property-changed raised on set. |
| SHELL-13 | critical | Opening any screen must construct exactly one view model, set its DataContext once by navigation, and issue exactly one API call per data set on that screen. | PERF-02 CODE-05 | UI test with construction counter and API call counter per navigation equal to 1. |
| SHELL-14 | high | View code-behind must contain no API, repository, SQL or business calls. | CODE-06 | Architecture test (grep/Roslyn) over Views/**/*.xaml.cs. |
| SHELL-15 | high | The client assemblies must not reference any data-access or SQL assembly, and view models must not inherit from a repository base. | CODE-07 CODE-24 | Architecture test on assembly references and base types. |
| SHELL-16 | high | Identity and role must come from a session issued by the API (not Thread.CurrentPrincipal), and logout must clear the session and tokens so that a later call is anonymous. | CODE-17 SEC-10 | Unit test: after logout the API client sends no token; role is read from session on any thread. |
| SHELL-17 | medium | A single navigation and session service must own window lifetime and logout, so logout runs once, closes the old window and disposes the previous main view model. | CODE-35 | UI test: logout leaves exactly one login window and no live MainViewModel instance. |
| SHELL-18 | medium | The shell must offer one account control (avatar, name, chevron, at least 32x32 DIP, accessible name 'Account menu') opening a menu with Profile, Change password and Sign out, reachable by keyboard. | UX-06 | UI capture and UIA test: control name, size and menu items. |
| SHELL-19 | high | The window chrome must support edge and corner resizing, Snap Layouts, double-click maximize, a restore glyph while maximized, Alt+Space, correct per-monitor maximize and 46x32 caption buttons whose click area equals their highlight, implemented once and reused by every window. | UX-01 UX-12 CODE-34 | UI automation: resize from each edge, maximize on a second monitor size, hit-test of caption buttons. |
| SHELL-20 | medium | The main window must have MinWidth 1024 and MinHeight 640 DIP, and every screen must reflow without clipped or overlapping content from 1024x640 to 2560x1440 at 100, 125, 150 and 200 percent scale. | UX-02 | Off-screen captures of each screen at the size/scale matrix; automated clipping check. |
| SHELL-21 | medium | The sidebar must have at most 8 grouped destinations, must not scroll at 640 DIP height, must keep the logo pinned outside the scroll region, and each menu label must equal its page title. | UX-03 | UI test: label-equals-title for each destination; capture at 1024x640. |
| SHELL-22 | high | Destinations a role cannot use must be hidden, and an access-denied state must offer 'Back to Dashboard' as the primary action and never force sign-out. | UX-08 | UI test as non-admin: Staff destinations absent; forced access-denied view has Back action. |
| SHELL-23 | low | Window titles must read 'Lifestream' on the login window and 'Lifestream - <page title>' in the shell, updated on every navigation. | UX-07 | UI test: window title after each navigation. |
| SHELL-24 | medium | List screens must keep filters, sort and scroll position for the session, and leaving a form with unsaved changes must prompt 'Discard changes?'. | UX-05 | UI test: set filter, navigate away and back, filter persists; dirty form shows prompt. |
| SHELL-25 | medium | All colours, spacing, radii and motion values must be defined as tokens in the theme files, views must contain no hex colour literals, and an automated test must confirm every text/background token pair meets 4.5:1 (3:1 for non-text and large text). **OWNER:** Colour direction: keep the red-forward look or neutral surfaces? Recommended default: neutral dark surfaces with brand red only as accent (the audit's token set), light theme later. | UX-44 UX-46 CODE-46 | Token contrast test (script over theme file); grep for hex literals in views is empty. |
| SHELL-26 | medium | The client must use one bundled font family and a 6-step type scale (display, title, heading, body, label, caption) with no text below 12 DIP and body and data text at 14 DIP. | UX-45 CODE-46 | Test: font file is an embedded resource; XAML scan finds only scale tokens; no font size under 12. |
| SHELL-27 | high | The shared Button component must provide primary, secondary, ghost, danger and icon variants, each with distinct idle, hover, pressed, focus, busy and disabled visuals defined by tokens, with no layout shift on hover or selection (including navigation items) and 100-150 ms transitions that honour the Windows animation-effects setting. | UX-41 UX-49 | Off-screen capture of every variant x state; test that hover does not change layout bounds; animations disabled when the system setting is off. |
| SHELL-28 | critical | A skeleton loader component must appear within 100 ms of any data request, match each surface's layout, be replaced in place with no layout shift, be static under reduced motion, and switch to an error state after 10 seconds. | UX-40 PERF-01 CODE-19 | UI test with a delayed stub API: skeleton visible at 100 ms, no bounds change when data arrives, error state at 10 s. |
| SHELL-29 | critical | Shared empty-state and error-state components must show a plain-language cause with an action (Add, Clear filters, Retry), with technical detail behind a 'Details' control. | UX-40 | UI capture of both states; Retry test re-issues the request. |
| SHELL-30 | medium | The toast component must name the affected record, auto-dismiss after 6 to 10 seconds, pause on hover or focus, expose a View or Undo action, and be announced to screen readers. | UX-48 | UI test: toast text, timing, action invocation, UIA live-region event. |
| SHELL-31 | critical | The confirmation dialog must name the record, put initial focus on Cancel, close on Esc, and label its danger button with the verb and object (for example 'Delete donor'). | UX-31 | UI test: focus owner, Esc result, button text. |
| SHELL-32 | high | Inline validation must show a per-field message with icon and text under the field on blur and on submit, announce it to screen readers, and move focus to the first invalid field. | UX-33 | UI test: submit empty form; messages present, UIA live announcements raised, focus on first invalid field. |
| SHELL-33 | high | Every input must have a persistent visible label associated with the control, placeholders (watermarks) must never be part of the value, and every combo box must be keyboard-focusable, bound to typed items and offer an explicit 'All' choice where filtering. | UX-34 UX-17 CODE-27 CODE-47 | XAML scan: no Focusable=False combos, no 'Search...' Text values; UIA LabeledBy check. |
| SHELL-34 | high | Every action in the client must be operable by keyboard alone with tab order following visual order, a visible 2 px focus ring at 3:1 contrast, and shortcuts Ctrl+F (search), Ctrl+N (new), Enter (submit) and Esc (cancel dialogs). | UX-42 | UI automation keyboard-only walkthrough of each screen; focus ring contrast test. |
| SHELL-35 | high | Every interactive control must expose a name, role and state to UI Automation, status messages must be live regions, and each chart must have an equivalent data table, passing Accessibility Insights FastPass with zero failures. | UX-43 | Run Accessibility Insights/UIA scan over each screen; assert zero failures. |
| SHELL-36 | medium | All dates must be displayed through one formatter in an unambiguous format (for example '05 Mar 2022' and '05 Mar 2022 14:30'), and date pickers must use the same format. **OWNER:** Which date format? Recommended default: 'dd MMM yyyy' (05 Mar 2022) everywhere. | UX-47 | Unit test of formatter; XAML scan finds no other StringFormat for dates. |
| SHELL-37 | critical | A record-picker control (searchable async combo for donor, patient, staff, appointment and request showing name, id and key detail) must replace every free-typed id field, with skeleton rows while searching and a summary chip after selection. | UX-29 UX-04 | UI test: type a name, pick a record, chip shows summary; XAML scan finds no 'Enter ... ID' TextBoxes. |
| SHELL-38 | high | The data grid component must virtualise rows so that 10,000 loaded items create fewer than 300 DataGridRow instances, with every screen using it. | PERF-07 | UI test: load 10,000 items, count DataGridRow in the visual tree. |
| SHELL-39 | medium | The data grid must size columns to content with minimum widths, show a tooltip for truncated cells, show sort indicators and keep the sort across filtering and paging, and use 14 DIP text at 36 to 40 DIP row height with row separators only and a selection style meeting 4.5:1. | UX-21 UX-22 UX-23 | UI capture at 1024 width (no clipped header), sort-state test across filter, contrast test on selected row. |
| SHELL-40 | medium | The status chip component must show an icon, text and a token colour for every status (Pending, Completed, Cancelled, Incomplete, Low stock, Expiring) so meaning never depends on colour alone. | UX-24 | UI capture in greyscale shows icon and text for each status. |
| SHELL-41 | critical | Masked password entry must use one control with an optional show toggle, and no screen may bind a stored password to a visible control. | CODE-39 UX-27 | XAML scan: no TextBox bound to a password; UI capture of staff and profile screens shows no password text. |
| SHELL-42 | medium | Display defaults such as the avatar must come from a view-side converter using one shared frozen resource loaded once, and must never be written into the data model or sent to the API. | CODE-32 PERF-15 | Unit test: converting a null photo leaves the model null; default image instance is shared and frozen. |
| SHELL-43 | high | Photos shown in the client must be decoded at a limited pixel size (DecodePixelWidth), and shipped images must be pre-sized for display (no asset over 50 KB at display size under 100 px) with unused images removed from the build. | PERF-09 PERF-17 CODE-22 | UI test: staff list of 110 loads under 200 ms and under 50 MB growth; build script checks asset sizes and unused assets. |
| SHELL-44 | medium | At startup the client must check the release feed for updates and show an 'update ready' prompt when a newer version exists. | SEC-11 | Client test with a fake feed: newer version raises the prompt; same version does not. |

## Client features (client-features)

| Id | Sev | Requirement | Sources | Proof |
|---|---|---|---|---|
| FEAT-01 | high | Every list screen must show one page of at most 50 rows with a 'Showing X-Y of N' count and fetch further pages from the API. | PERF-06 UX-20 | UI capture with 100x data: count text present, only one page requested; last row never half-clipped. |
| FEAT-02 | high | Every list screen must apply filters as the user types (300 ms debounce) or on Enter, offer 'Clear filters', and send no text filter when the search box is empty. | UX-17 UX-18 CODE-27 CODE-37 | UI test per list: type text, assert one debounced call; empty search sends no text parameter; Clear resets. |
| FEAT-03 | critical | Every list screen must show skeleton rows while loading, an empty state when there are no rows, and an error state with Retry on failure. | UX-40 CODE-19 | Per-screen UI captures with a delayed, an empty and a failing stub API. |
| FEAT-04 | critical | Each entity (donors, patients, appointments, requests, receipts, staff) must have one screen with a list and detail pane where create, edit, delete and related actions act on the selected record, with no separate 'Info' or 'Manage Operations' screens. | UX-04 UX-29 UX-36 | UI navigation test: destinations list contains no Info/Operations screens; every action reachable from the selected record. |
| FEAT-05 | critical | Editing a donor, patient or staff record must open it pre-filled, send only the fields the user changed, and be able to clear optional fields. | UX-30 CODE-03 | UI test with a recording stub: change one field, assert request body contains only that field. |
| FEAT-06 | critical | Every delete must open a confirmation naming the record, change the UI only after the API confirms, show a Not found or conflict message on failure leaving the list unchanged, and offer a 10-second Undo toast on success. | UX-31 CODE-12 CODE-13 | UI test with stub 409 and 404: row remains; success shows Undo toast. |
| FEAT-07 | high | Donor and patient forms must mark required fields, validate before submit (DOB in the past, donor age 18 or more, numeric-only contact, no MinValue dates) and show the API's field errors inline. | UX-33 CODE-14 UX-34 | UI test: invalid inputs show inline messages and send no request. |
| FEAT-08 | medium | Forms must use a left-aligned two-column grid with related fields grouped, the primary action bottom-right, and a Cancel that is always enabled and asks for confirmation only when the form is dirty. | UX-35 CODE-48 | UI capture of each form; test Cancel enabled on a clean form and prompts only when dirty. |
| FEAT-09 | low | Booking or recording a donation must show an eligibility banner (age, days since last donation, medical flag) and require an override reason for an ineligible donor. | UX-38 | UI test with ineligible stub donor: banner shown, submit blocked until reason entered. |
| FEAT-10 | high | The appointment screen must filter by donor through the API using the donor id of each appointment and show correct results even when donor ids differ from appointment ids. | CODE-04 | UI/API test with mismatched ids: only that donor's appointments are listed. |
| FEAT-11 | critical | The appointment booking form must use a date picker that blocks past dates and a time-slot picker built from clinic hours showing taken slots, and no combination of inputs (including no time chosen) may crash the app. | UX-32 CODE-10 | UI fuzz test over the form fields: no unhandled exception; past dates unselectable. |
| FEAT-12 | low | Appointment times must be parsed with an exact format so that 12:00 AM, 12:00 PM and 1:00 PM resolve to the correct hours. | CODE-49 | Unit test of the time parser for 12:00 AM, 12:00 PM, 1:00 PM and 11:00 AM. |
| FEAT-13 | low | Editing an appointment must send the date and the time, preserving the existing time when only the date is changed. | CODE-50 | UI test: edit date only on an 11:00 appointment; request carries 11:00. |
| FEAT-14 | high | The appointments, requests and receipts lists must show the person's name with the id as secondary text and open the referenced record, hide always-empty or constant columns, show times only when present, and show statuses as chips. | UX-19 UX-24 | UI capture with demo data; test that the Request ID column is hidden for donation rows. |
| FEAT-15 | high | Filtering requests by patient name or blood type must complete with one API call and show the patient name column that explains the match. | PERF-05 UX-19 | UI test: filter click makes 1 API call; name column visible. |
| FEAT-16 | high | Recording a donation must be an action on an appointment and fulfilling a request must be an action on a request, each opening a focused dialog with its own validation, feedback and receipt issue. | UX-36 UX-04 | UI test: Record donation from an appointment row pre-fills donor and appointment; Fulfil checks stock. |
| FEAT-17 | high | The receipts screen must support text search and show the staff, request and appointment names instead of bare ids. | UX-26 UX-19 | UI test: search by patient name finds the receipt; capture shows names. |
| FEAT-18 | high | Each receipt must open a printable detail view with Print and Save as PDF actions, and a duplicate receipt must always be producible. | UX-26 | UI test: Save as PDF creates a non-empty PDF containing receipt number and names. |
| FEAT-19 | high | The stock screen must show one tile per blood type with units available, units expiring within 7 days and a below-threshold state (icon, text and token colour), and its filter must apply immediately. | UX-25 UX-15 CODE-37 | UI capture with low-stock stub; filter change issues a request without a Search button. |
| FEAT-20 | critical | The staff list must not show a password column, must load rows without photo bytes, show thumbnails lazily with the shared default avatar, and its salary-range inputs must accept digits only and show a message when From exceeds To. | UX-27 UX-28 PERF-15 PERF-08 | UI capture shows no password column; test non-digit input rejected; list of 110 staff loads under 200 ms. |
| FEAT-21 | medium | The new-staff form must start with empty date of birth and salary (no fake defaults) and must collect a password only through a masked field with confirmation. | UX-37 CODE-33 | UI test: new form fields empty; submit with mismatched confirmation is blocked. |
| FEAT-22 | critical | Admins must have a 'Reset password' action on a staff record and must never be shown any password. | UX-27 SEC-09 | UI test: reset action calls the API; no screen shows password text. |
| FEAT-23 | critical | The staff screen must disable Delete on the signed-in admin's own record with a tooltip, and require confirmation for every other delete. | CODE-33 UX-31 | UI test: own row delete disabled; other row opens confirmation. |
| FEAT-24 | high | The profile screen must show read-only data as label and value text meeting 4.5:1, format dates, and enable Save only when something changed. | UX-39 CODE-15 | UI capture and contrast test; view-model test: CanSave false on a clean form. |
| FEAT-25 | high | Changing a password must use a separate dialog with current, new and confirm masked fields and must work for users with or without a stored photo. | CODE-15 UX-39 SEC-09 | UI test with a null-photo user: change password succeeds with correct current password. |
| FEAT-26 | high | Saving a profile must send the photo only when the user has replaced it. | CODE-15 | UI test: edit name only; request body has no photo field. |
| FEAT-27 | high | The dashboard must render from one API call, show its skeleton within 100 ms, render charts within 300 ms of data arrival, and use no blur effect on cards. | PERF-16 PERF-04 | UI test with stub API: time to skeleton and to chart rendered; XAML scan finds no DropShadowEffect with blur on cards. |
| FEAT-28 | high | The donations chart must aggregate by month, start its y-axis at 0, offer a period selector defaulting to the last 12 months, let the user toggle blood types, offer a 'View as table' alternative, and label its x-axis from the same bucket value used for the data. | UX-13 CODE-09 CODE-40 CODE-36 | UI capture; test that axis minimum is 0 and axis title equals bucket unit; table view lists the same values. |
| FEAT-29 | medium | The demographics chart must be a labelled bar or stacked bar that includes every stored gender value, with data labels meeting 4.5:1 contrast. | UX-14 | UI capture with an Other donor; contrast test on label colour. |
| FEAT-30 | medium | Dashboard KPI cards must open the matching list pre-filtered, and a stock card must flag every blood type below threshold with icon and text. | UX-15 | UI test: click Pending Appointments opens Appointments with status Pending; stock card shows flagged types. |
| FEAT-31 | low | Numeric columns in the dashboard totals table must be right-aligned under right-aligned headers, with no per-cell borders. | UX-16 | UI capture and bounds test of header versus cell alignment. |

## Infrastructure (infra)

| Id | Sev | Requirement | Sources | Proof |
|---|---|---|---|---|
| INFRA-01 | critical | CI must scan every built artifact (binaries, configs, installers, container image, logs) for database credentials in UTF-8 and UTF-16 (Password=, User ID=, connection strings) and fail on any hit. | SEC-01 | CI step output: zero hits on a clean build; a planted test string makes the step fail. |
| INFRA-02 | critical | No live credential may be reachable from any public ref of the repository (history rewritten or a fresh repository used), and a secret scanner with push protection must run in CI. **OWNER:** Old history leaks credentials: rewrite history or start a fresh repository? Recommended default: fresh repository with new history, and rotate or delete every credential seen in old commits. | SEC-03 | gitleaks over full history returns zero findings; GitHub push protection enabled (gh api). |
| INFRA-03 | critical | The two old database servers (database-server-bds and lifestream-database-server) and all old client releases (1.0.0.7 to 1.0.1.0) must be deleted, rotated or unpublished. **OWNER:** Confirm the two older servers are deleted and the old ClickOnce releases unpublished. Recommended default: yes, delete both servers and unpublish all old releases. | SEC-01 SEC-11 | az sql server list shows neither server; release listing shows none of the old versions. |
| INFRA-04 | critical | The SQL server firewall must contain no 0.0.0.0-255.255.255.255 rule and no 'allow all Azure services' rule, only the API environment's outbound addresses. | SEC-02 | az sql server firewall-rule list shows only the API outbound IPs. |
| INFRA-05 | medium | Microsoft Entra-only authentication must be enabled on the SQL server so that SQL logins (including the old admin login) cannot be used. | SEC-12 | az sql server ad-only-auth get returns true. |
| INFRA-06 | high | The API must connect to Azure SQL through its managed identity as a contained database user holding only read, write and execute permissions, and schema changes must run under a separate migration identity with no password. | SEC-08 SEC-12 | Test connection as the API identity: SELECT/INSERT succeed, CREATE TABLE fails; no connection string contains a password. |
| INFRA-07 | high | SQL server auditing must be enabled and an application audit trail must record who changed data. **OWNER:** Enable Defender for SQL and Log Analytics auditing (paid) or only the application audit table and low-cost auditing? Recommended default: application audit table plus server auditing to a low-cost storage destination with short retention, Defender off to keep running cost at zero. | SEC-08 | az sql server audit-policy show returns Enabled; see the application audit requirement. |
| INFRA-08 | low | The SQL server and database must have a delete lock and a documented backup retention of at least 14 days. **OWNER:** Backup retention and long-term copy? Recommended default: 14-day point-in-time retention if the free tier allows it, a delete lock, and no long-term retention (cost). | SEC-17 | az lock list shows delete locks; az sql db show backup retention is 14 days or more. |
| INFRA-09 | medium | The API must run on Azure Container Apps Consumption with minimum replicas 0, a maximum of 2, a startup probe on /healthz, external HTTPS only, and a budget alert configured. | PERF-10 | az containerapp show: minReplicas 0, maxReplicas <= 2, probes set; HTTP redirected to HTTPS; az consumption budget exists. |
| INFRA-10 | medium | The database must be an Azure SQL serverless free-offer database with auto-pause on free-limit exhaustion, so that a surprise bill is impossible. **OWNER:** Which Azure SQL tier? Recommended default: free serverless with AutoPause on exhaustion; accept a cold start of up to about a minute with the 'waking up' state. | PERF-10 | az sql db show: useFreeLimit true, freeLimitExhaustionBehavior AutoPause. |
| INFRA-11 | high | The demo dataset must live in a database separate from any non-demo data, and the demo role must be unable to reach any other database. **OWNER:** Is a separate non-demo production database needed at all? Recommended default: no; a single demo database now, with a documented path to add a production one. | SEC-07 SEC-06 | az sql db list shows a separate demo database; identity permissions test. |
| INFRA-12 | medium | After every successful deploy only the newest 3 container image versions may remain in the registry, the deployed image must never be deleted, and the retained tag must pass docker manifest inspect. | (background (keep only latest 3 images)) | CI step after deploy lists package versions (3) and inspects the latest tag. |
| INFRA-13 | critical | Deployment must run from GitHub Actions using OIDC federation (no stored cloud secrets), triggered after build and tests pass, in a workflow separate from the PR build. | SEC-03 CODE-44 | Workflow file has id-token permission and no secret-valued Azure credentials; deploy job needs the test job. |
| INFRA-14 | low | CI must build, run all tests and analyzers on every pull request, and a pull request must fail when any test or analyzer fails. | CODE-44 | Open a PR with a failing test; check fails. |
| INFRA-15 | medium | The build must treat warnings as errors with nullable enabled and analyzers for unused code and usings and undisposed resources, and must fail on any violation. | CODE-22 CODE-23 CODE-25 | CI build with IDE0005 and CA2000 as errors passes on main; a planted unused using fails it. |
| INFRA-16 | medium | NuGet audit must fail the build on any high-severity advisory, Dependabot must be enabled, central package management must pin versions, and no unmaintained or unused package (System.Drawing.Common, LiveCharts 0.9.7) may be referenced. **OWNER:** Which chart library and tech choices? Recommended defaults: LiveCharts2 (LiveChartsCore.SkiaSharpView.WPF) for charts, CommunityToolkit.Mvvm with the generic host for DI and navigation, and EF Core for data access. | SEC-16 CODE-42 CODE-23 | dotnet list package --vulnerable --deprecated returns none; Directory.Packages.props exists; Dependabot config present. |
| INFRA-17 | medium | Each client release must publish an installer, update packages for the update feed and a SHA-256 checksum file to the release page, and be Authenticode-signed when a signing certificate is configured. **OWNER:** Code signing? Recommended default: ship unsigned at first with published SHA-256 checksums and a README note about the SmartScreen prompt; add signing when a certificate or free OSS signing is available. | SEC-11 | Release workflow output lists installer, update feed files and checksum; signature verified when a cert secret exists. |
| INFRA-18 | high | The repository must ignore *.bak, *.bacpac, *.rar, *.pfx, publish/ and .env, must track no database dump, backup or autorun.inf, must hold docs and screenshots under docs/, and its README must match the current version. | CODE-43 SEC-18 SEC-06 | git ls-files finds none of those patterns; .gitignore contains the patterns; README version check. |
| INFRA-19 | low | README and store screenshots must be regenerated by a script from the demo dataset only. | UX-50 | Script run in CI produces the screenshots; images contain only demo data. |
| INFRA-20 | medium | A written retention and deletion rule for donor and patient records must exist in the repository docs, and the public demo must use only synthetic data. **OWNER:** Is any data real and which jurisdiction applies? Recommended default: all demo data is synthetic; document 'delete on request, review annually' as the retention rule. | SEC-15 | Docs file exists and CI checks it; seed scan (see seed requirement) passes. |

## Moot findings

| Id | Reason |
|---|---|
| CODE-24 | The rewrite replaces hand-written SqlDataReader loops with a typed data layer (EF Core) and a client with no SQL; parameterised-query discipline is kept under the API data-access requirement. |
| CODE-41 | The rewrite is a new SDK-style .NET 11 solution with PackageReference and central package management by construction (fixed by the background). |
| CODE-45 | The new solution is named for the product and organised by feature by construction; layering is enforced by the architecture tests under the client-shell requirements. |

## Owner questions

| Requirement | Question and recommended default |
|---|---|
| DB-06 | Performance budgets: test at 1x demo size only, 100x only, or both? Recommended default: both (1x for UI timing, 100x for query plans). |
| DB-11 | Should staff photos stay in the database? Recommended default: yes, in a separate StaffPhoto table with 256 px thumbnails (no extra Azure resource, so zero extra cost); move to object storage only if size becomes a problem. |
| DB-15 | Is any row or photo in the old BloodBank.bacpac a real person, and did those logins ever work in production? Recommended default: treat all of it as unusable, discard it, and generate fresh synthetic data. |
| DB-17 | Keep the Attendance table (no UI in the old app, 83 unused rows)? Recommended default: drop it, keep BloodBag. |
| API-05 | What may the demo user do? Recommended default: ordinary staff role (read and write sample donors, patients, appointments, requests, receipts), no staff management, no password change. |
| API-11 | Are the documented clinical eligibility rules (age 18+, 3 months since last donation, medical issue flag) the ones to enforce? Recommended default: yes, with an override reason. |
| SHELL-04 | How is the public demo login presented? Recommended default: a 'Try the demo' button on the login screen that signs into the sample-data account, with no credentials printed. |
| SHELL-25 | Colour direction: keep the red-forward look or neutral surfaces? Recommended default: neutral dark surfaces with brand red only as accent (the audit's token set), light theme later. |
| SHELL-36 | Which date format? Recommended default: 'dd MMM yyyy' (05 Mar 2022) everywhere. |
| INFRA-02 | Old history leaks credentials: rewrite history or start a fresh repository? Recommended default: fresh repository with new history, and rotate or delete every credential seen in old commits. |
| INFRA-03 | Confirm the two older servers are deleted and the old ClickOnce releases unpublished. Recommended default: yes, delete both servers and unpublish all old releases. |
| INFRA-07 | Enable Defender for SQL and Log Analytics auditing (paid) or only the application audit table and low-cost auditing? Recommended default: application audit table plus server auditing to a low-cost storage destination with short retention, Defender off to keep running cost at zero. |
| INFRA-08 | Backup retention and long-term copy? Recommended default: 14-day point-in-time retention if the free tier allows it, a delete lock, and no long-term retention (cost). |
| INFRA-10 | Which Azure SQL tier? Recommended default: free serverless with AutoPause on exhaustion; accept a cold start of up to about a minute with the 'waking up' state. |
| INFRA-11 | Is a separate non-demo production database needed at all? Recommended default: no; a single demo database now, with a documented path to add a production one. |
| INFRA-16 | Which chart library and tech choices? Recommended defaults: LiveCharts2 (LiveChartsCore.SkiaSharpView.WPF) for charts, CommunityToolkit.Mvvm with the generic host for DI and navigation, and EF Core for data access. |
| INFRA-17 | Code signing? Recommended default: ship unsigned at first with published SHA-256 checksums and a README note about the SmartScreen prompt; add signing when a certificate or free OSS signing is available. |
| INFRA-20 | Is any data real and which jurisdiction applies? Recommended default: all demo data is synthetic; document 'delete on request, review annually' as the retention rule. |

## Coverage table (finding id to requirement ids)

| Finding | Requirements |
|---|---|
| SEC-01 | INFRA-01, INFRA-03 |
| SEC-02 | API-41, INFRA-04 |
| SEC-03 | INFRA-02, INFRA-13 |
| SEC-04 | DB-02, API-01, API-30 |
| SEC-05 | API-06, API-07 |
| SEC-06 | DB-15, API-39, API-40, INFRA-11, INFRA-18 |
| SEC-07 | API-04, API-05, API-40, API-41, SHELL-03, SHELL-04, INFRA-11 |
| SEC-08 | DB-16, API-43, INFRA-06, INFRA-07 |
| SEC-09 | DB-02, API-34, FEAT-22, FEAT-25 |
| SEC-10 | API-01, API-02, API-03, API-04, API-42, SHELL-06, SHELL-16 |
| SEC-11 | SHELL-44, INFRA-03, INFRA-17 |
| SEC-12 | INFRA-05, INFRA-06 |
| SEC-13 | API-12, API-38, API-43 |
| SEC-14 | DB-07, API-08, API-09, API-36 |
| SEC-15 | API-33, INFRA-20 |
| SEC-16 | INFRA-16 |
| SEC-17 | INFRA-08 |
| SEC-18 | API-30, API-36, INFRA-18 |
| SEC-19 | API-32 |
| PERF-01 | SHELL-09, SHELL-28 |
| PERF-02 | SHELL-13 |
| PERF-03 | DB-12 |
| PERF-04 | API-26, FEAT-27 |
| PERF-05 | API-22, FEAT-15 |
| PERF-06 | API-21, API-23, FEAT-01 |
| PERF-07 | SHELL-38 |
| PERF-08 | DB-11, API-31, FEAT-20 |
| PERF-09 | API-32, SHELL-43 |
| PERF-10 | API-37, API-38, SHELL-05, SHELL-07, SHELL-08, INFRA-09, INFRA-10 |
| PERF-11 | API-14 |
| PERF-12 | DB-04, DB-05, DB-06 |
| PERF-13 | DB-13, DB-14, API-20 |
| PERF-14 | DB-10, API-18, API-19, API-20, API-39 |
| PERF-15 | API-31, SHELL-42, FEAT-20 |
| PERF-16 | FEAT-27 |
| PERF-17 | SHELL-43 |
| CODE-01 | SHELL-01, SHELL-02 |
| CODE-02 | API-16, API-18 |
| CODE-03 | API-15, FEAT-05 |
| CODE-04 | API-17, FEAT-10 |
| CODE-05 | SHELL-13 |
| CODE-06 | SHELL-14 |
| CODE-07 | SHELL-15 |
| CODE-08 | SHELL-09 |
| CODE-09 | API-26, FEAT-28 |
| CODE-10 | SHELL-01, FEAT-11 |
| CODE-11 | API-12 |
| CODE-12 | FEAT-06 |
| CODE-13 | API-13, FEAT-06 |
| CODE-14 | API-09, API-10, FEAT-07 |
| CODE-15 | FEAT-24, FEAT-25, FEAT-26 |
| CODE-16 | SHELL-03 |
| CODE-17 | API-06, SHELL-16 |
| CODE-18 | DB-03 |
| CODE-19 | SHELL-11, SHELL-28, FEAT-03 |
| CODE-20 | SHELL-12 |
| CODE-21 | SHELL-10 |
| CODE-22 | SHELL-43, INFRA-15 |
| CODE-23 | INFRA-15, INFRA-16 |
| CODE-24 | SHELL-15, moot |
| CODE-25 | INFRA-15 |
| CODE-26 | API-22 |
| CODE-27 | SHELL-33, FEAT-02 |
| CODE-28 | DB-07, API-45 |
| CODE-29 | DB-08 |
| CODE-30 | DB-09 |
| CODE-31 | API-36 |
| CODE-32 | SHELL-42 |
| CODE-33 | API-35, FEAT-21, FEAT-23 |
| CODE-34 | SHELL-19 |
| CODE-35 | SHELL-17 |
| CODE-36 | API-27, FEAT-28 |
| CODE-37 | FEAT-02, FEAT-19 |
| CODE-38 | API-37, API-38, SHELL-07 |
| CODE-39 | API-30, SHELL-41 |
| CODE-40 | FEAT-28 |
| CODE-41 | moot |
| CODE-42 | INFRA-16 |
| CODE-43 | DB-01, INFRA-18 |
| CODE-44 | INFRA-13, INFRA-14 |
| CODE-45 | moot |
| CODE-46 | SHELL-25, SHELL-26 |
| CODE-47 | SHELL-33 |
| CODE-48 | FEAT-08 |
| CODE-49 | FEAT-12 |
| CODE-50 | API-44, FEAT-13 |
| CODE-51 | API-25 |
| CODE-52 | DB-17 |
| UX-01 | SHELL-19 |
| UX-02 | SHELL-20 |
| UX-03 | SHELL-21 |
| UX-04 | SHELL-37, FEAT-04, FEAT-16 |
| UX-05 | SHELL-24 |
| UX-06 | SHELL-18 |
| UX-07 | SHELL-23 |
| UX-08 | SHELL-22 |
| UX-09 | SHELL-03, SHELL-04 |
| UX-10 | API-38, SHELL-02, SHELL-07 |
| UX-11 | SHELL-05, SHELL-06 |
| UX-12 | SHELL-19 |
| UX-13 | API-27, FEAT-28 |
| UX-14 | API-28, FEAT-29 |
| UX-15 | API-29, FEAT-19, FEAT-30 |
| UX-16 | FEAT-31 |
| UX-17 | SHELL-33, FEAT-02 |
| UX-18 | API-23, FEAT-02 |
| UX-19 | API-24, FEAT-14, FEAT-15, FEAT-17 |
| UX-20 | FEAT-01 |
| UX-21 | SHELL-39 |
| UX-22 | SHELL-39 |
| UX-23 | SHELL-39 |
| UX-24 | SHELL-40, FEAT-14 |
| UX-25 | API-29, FEAT-19 |
| UX-26 | FEAT-17, FEAT-18 |
| UX-27 | API-34, SHELL-41, FEAT-20, FEAT-22 |
| UX-28 | FEAT-20 |
| UX-29 | SHELL-37, FEAT-04 |
| UX-30 | API-15, FEAT-05 |
| UX-31 | SHELL-31, FEAT-06, FEAT-23 |
| UX-32 | API-10, FEAT-11 |
| UX-33 | API-09, SHELL-32, FEAT-07 |
| UX-34 | SHELL-33, FEAT-07 |
| UX-35 | FEAT-08 |
| UX-36 | FEAT-04, FEAT-16 |
| UX-37 | FEAT-21 |
| UX-38 | API-11, FEAT-09 |
| UX-39 | API-34, FEAT-24, FEAT-25 |
| UX-40 | SHELL-28, SHELL-29, FEAT-03 |
| UX-41 | SHELL-27 |
| UX-42 | SHELL-34 |
| UX-43 | SHELL-35 |
| UX-44 | SHELL-25 |
| UX-45 | SHELL-26 |
| UX-46 | SHELL-25 |
| UX-47 | SHELL-36 |
| UX-48 | SHELL-30 |
| UX-49 | SHELL-27 |
| UX-50 | INFRA-19 |

Check: security.md 19 ids, performance.md 17, code.md 52, ui-ux.md 50; total 138; table rows 138; unmapped 0.
