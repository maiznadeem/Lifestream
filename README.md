# Lifestream 2.1

Lifestream is a blood-bank management desktop app: donors, patients, appointments, blood requests, stock and receipts,
with a dashboard. Version **2.1.6** is a ground-up rewrite of the original course project on .NET 11.

What changed for users is in [CHANGELOG.md](CHANGELOG.md).

## Install

1. Open the [v2.1.6 release](https://github.com/maiznadeem/lifestream/releases/tag/v2.1.6) and download `Lifestream-win-Setup.exe` (and `SHA256SUMS.txt`). Windows 10 or 11, 64-bit.
2. Check the download. In PowerShell, in the folder you saved it to:

   ```powershell
   $expected = (Get-Content .\SHA256SUMS.txt | Where-Object { $_ -like '*Lifestream-win-Setup.exe' } | Select-Object -First 1) -split ' ' | Select-Object -First 1
   $actual = (Get-FileHash .\Lifestream-win-Setup.exe -Algorithm SHA256).Hash
   if ($actual -eq $expected) { 'MATCH: the download is intact' } else { 'NO MATCH: delete the file and download it again' }
   ```

   It prints `MATCH` when the SHA-256 of your download is the one listed in `SHA256SUMS.txt`, and `NO MATCH` otherwise. If it does not match, delete the file and download it again.

3. Run the installer. The installer is **not code-signed** (a certificate costs money every year), so Windows SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**. The app installs for your user only, needs no administrator rights, and updates itself when a newer release is published.
4. Start Lifestream and press **Try the demo** (see below): no account, user name or password is needed.

## Try it (demo login)

Start the app and press **Try the demo**. No account, username or password is needed. The demo runs against synthetic
data only (see [docs/data-retention.md](docs/data-retention.md)) and is reset every day. The first request after a quiet period can take about a minute and a half while the server wakes up (first the server starts, then its database); the app says which while it waits. The demo database runs on a free plan with a monthly allowance: if that is used up, the app says so and the demo is back on the 1st of the next month.

The demo clinic has a day in progress: today's appointments are in it, the earlier ones already donated, the rest
still to come. It keeps its hours in UTC (09:00 to 16:30), so they appear at those hours in your own time.

## Screenshots

Rendered off-screen through the real app from the real demo dataset (never real data) by `tools/generate-screenshots.ps1`:
it fills a fresh local database with the demo data generator, starts the real API over it, signs in with "Try the demo" and
captures every screen. A screen that shows an error or empty state, a dashboard whose counts differ from the demo
dataset's, or a day without appointments fails the run. The times on them are the demo clinic's own (UTC), whatever the
time zone of the PC that took them, and today's agenda is as far on as the clock was then (`screenshots.json` records it).
Regenerate them by running that script, never edit them by hand.

| | |
|---|---|
| ![Sign in](docs/screenshots/login.png) | ![Dashboard](docs/screenshots/dashboard.png) |
| ![Donors](docs/screenshots/donors.png) | ![Patients](docs/screenshots/patients.png) |
| ![Appointments](docs/screenshots/appointments.png) | ![Requests](docs/screenshots/requests.png) |
| ![Stock](docs/screenshots/stock.png) | ![Receipts](docs/screenshots/receipts.png) |
| ![Staff](docs/screenshots/staff.png) | ![Profile](docs/screenshots/profile.png) |

## Architecture

```
Lifestream.Desktop (WPF, .NET 11)  --HTTPS + JWT-->  Lifestream.Api (ASP.NET Core 11, Azure Container Apps)
                                                          |
                                                          v  EF Core, managed identity
                                                     Azure SQL
```

| Project | What it holds |
|---|---|
| `src/Lifestream.Domain` | Enums (blood types, genders, shifts, statuses) and rules (eligibility, shelf life). No dependencies. |
| `src/Lifestream.Contracts` | Every request/response record, route, header and problem code shared by API and client. Frozen API surface. |
| `src/Lifestream.Data` | EF Core context, migrations, demo and scale data generators. Used only by the API. |
| `src/Lifestream.Api` | Minimal API, one folder per feature. The only code that talks to the database. |
| `src/Lifestream.Client` | View models, services and the API client. No WPF, EF Core or SqlClient reference. |
| `src/Lifestream.Desktop` | WPF views, design system, app host, Velopack updater. |
| `tests/*` | One test project per source project, plus off-screen UI render tests. |

The full design is in [docs/superpowers/specs](docs/superpowers/specs/2026-11-19-lifestream-overhaul-design.md); every
requirement, with the wording each proof is written against, is in [docs/requirements.md](docs/requirements.md).

## Build and test

Requirements: the .NET SDK version in `global.json` (11.0.x) on Windows 10/11 for the desktop app. SQL Server is only
needed for the data and API tests (set `LIFESTREAM_TEST_SQL`; it defaults to the local default instance with Windows
authentication).

```
dotnet build Lifestream.slnx -c Release
dotnet test  Lifestream.slnx -c Release --no-build
```

The build treats every warning as an error (nullable enabled, analyzers for unused code and usings and undisposed
resources, NuGet audit at high severity). Package versions are pinned centrally in `Directory.Packages.props`.

## Run

```
dotnet run --project src/Lifestream.Api         # API on http://localhost:5080, GET /healthz
dotnet run --project src/Lifestream.Desktop     # desktop client
```

## Releases

Installers are published on the [GitHub Releases page](https://github.com/maiznadeem/lifestream/releases) with SHA-256 checksums (see
[Install](#install)). They are not code-signed, so Windows SmartScreen shows a "More info, Run anyway" prompt. The text of each release is its
section of [CHANGELOG.md](CHANGELOG.md).

A release is made on the maintainer's PC, not in GitHub Actions (the minutes are limited). Once the version in `Directory.Build.props`
and its `CHANGELOG.md` section are on `master`, and the build and the tests have passed locally:

```
pwsh tools/release-local.ps1 -Version <version> -DryRun   # publish, scan, pack, scan, checksums: nothing leaves the PC
pwsh tools/release-local.ps1 -Version <version>           # the same, then the GitHub release and its tag
```

The script refuses unless the working tree is clean, `HEAD` is the commit `master` points at on GitHub, the version matches and the
tag is not taken. It publishes the desktop app (self-contained, win-x64, ReadyToRun), scans the publish folder for credentials, packs
the installer and the update packages with Velopack, scans those, writes `SHA256SUMS.txt`, creates the release as a draft at that
commit, uploads the assets, compares them with what GitHub received, and only then publishes it, which creates the tag `v<version>`.
A tag starts no workflow. `release.yml` does the same on a GitHub runner and is started by hand only, as a fallback (see its header).

## Continuous integration

What starts by itself is short, because the whole build and every test are run locally before a push:

| Workflow | Starts on | Does | Time |
|---|---|---|---|
| `ci.yml` | pull request, push to `master` | job `fast`: builds Client and Domain with their tests and runs them | 90 second budget, checked by the job itself |
| `secret-scan.yml` | pull request, push to `master` | job `repository`: credential scan of the source and of the new commits | about 20 seconds |
| `secret-scan.yml` | Mondays 04:17 UTC | job `history`: credential scan of the whole history, every branch and tag | under a minute |
| `deploy-api.yml` | push to `master` that changes the API, its libraries or `infra/` | builds and scans the image, migrates, updates the app, checks `/healthz` | about 5 minutes (the migration and the rollout are Azure's time) |

Everything else runs only when started by hand (Actions > the workflow > Run workflow): the full `api`, `desktop` and `test-order`
jobs of `ci.yml`, the `desktop` and `api-image` scans of `secret-scan.yml` (a run by hand scans the whole history too), `release.yml`,
`screenshots.yml` and `sync-sql-firewall.yml`.

## Security

To report a vulnerability, and for what the old history holds (two 1.x database logins, neither of which works), see
[docs/SECURITY.md](docs/SECURITY.md).

## Repository rules

Database dumps and backups (`*.bak`, `*.bacpac`, `*.rar`), certificates (`*.pfx`), `publish/` output and `.env` files
are git-ignored and never committed. Documentation and generated screenshots live under `docs/`.
