# Performance results (2.1.6 + night-perf)

Measured on 2026-11-21 between 00:10 and 02:50 (Pakistan Standard Time, UTC+05:00) on branch `night-perf` (master `c7bfda9` + night-iso `06cbd9d` +
the commits named below), against the same tree without them. Budgets are design section 5.5 and are unchanged; each is stated with its statistic and sample.

**Verdict: the UI-thread gap budget (< 50 ms) is not shown met.** The work below makes every screen cheaper to build on its first and second visits
(interleaved measurements, 35 to 70 % less UI-thread work on a first visit), but no run of tonight can show the budget met: the PC was shared with other
agents' builds and tests (near 100 % CPU most of the night), and since 01:43 every test process runs on an isolated desktop (tests/isolated), a job at
below-normal priority whose windows draw in software. In those conditions the harness's own gap test missed on both builds, by amounts that are mostly the
machine (a 2-second stall on Donors in both builds, list API p95 over 100 ms). The cold dashboard budget (< 300 ms) was met in three of four harness runs
(253 to 274 ms) and missed in a dedicated comparison (median 331 ms, unchanged by this work). A run on a quiet PC is still owed for both.

| | |
|---|---|
| CPU | AMD Ryzen 9 5900X 12-Core Processor (12 cores, 24 threads); runs pinned to logical processors 8-15 (affinity 0xFF00), `DOTNET_PROCESSOR_COUNT=8` |
| Priority | normal for the harness and the diagnostic; the isolated desktop's job runs its test processes at below-normal |
| Memory / OS / SDK / power | 127.9 GB / Windows 11 Pro 10.0.26300 / .NET SDK 11.0.100 / Balanced |
| SQL Server | Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64) |
| Quiet check | not asserted: the machine was busy all night, so `-AssertBudgets` would refuse to start |
| Phases | `timing`, `1x`, `10x`, `100x`. `launch` was not run: it starts the published app, a real window on the desktop of the person at the PC (it now needs `-InSession`) |

## What changed

| Commit | Change |
|---|---|
| `7a8182c` | A side panel's content is built the first time the panel opens (`Controls/DeferredContent.cs`), on all seven panel screens. A closed panel's content was parsed, styled and bound at every navigation and never laid out. |
| `c818355` | The app runs without dynamic PGO (`<TieredPGO>false</TieredPGO>`). It ships ReadyToRun; with PGO on, every method that gets hot ran an instrumented copy before its optimized one, so the first two visits of every screen cost about twice the later ones. The Desktop test project (whose publish the perf pass runs) sets the same, checked by `RuntimeSettingsTests`. |
| `3fe1582` | `run-perf.ps1` runs every test process on the isolated desktop by default. |
| `9913bb0`, `a4148b5` | `PerfPhase=diag`: the gap test's visits in a fresh process, every dispatcher operation of 1 ms or more with the UI thread's own CPU cycles. |

## Before and after: first visits (the user's), interleaved

`PerfPhase=diag` over the 1x fixture (a page is 25 rows at any size), 5 fresh processes per build, alternating before / after on the same isolated desktop,
4 rounds of the nine screens each. "Mcycles" is the CPU the UI thread itself spent in the navigation's layout-and-render pass (QueryThreadCycleTime: not
counting time it waited for a processor, so it is what the machine's load disturbs least); the gap is the harness's statistic for one load. Medians over
the processes. Before: `06cbd9d`. After: `a4148b5`.

| Screen | first visit Mcycles before | after | change | second visit Mcycles before | after | first-visit gap ms before | after |
|---|---|---|---|---|---|---|---|
| Dashboard | 192 | 177 | -8 % | 271 | 116 | 64.9 | 72.4 |
| Donors | 558 | 313 | -44 % | 405 | 186 | 169.1 | 114.9 |
| Appointments | 630 | 371 | -41 % | 519 | 176 | 182.9 | 104.9 |
| Patients | 444 | 178 | -60 % | 416 | 146 | 136.7 | 52.2 |
| Requests | 484 | 199 | -59 % | 592 | 149 | 87.5 | 57.9 |
| Stock | 527 | 159 | -70 % | 366 | 123 | 154.3 | 50.4 |
| Receipts | 424 | 254 | -40 % | 324 | 134 | 110.5 | 82.2 |
| Staff | 358 | 133 | -63 % | 310 | 108 | 126.7 | 94.5 |
| Profile | 558 | 184 | -67 % | 397 | 204 | 101.5 | 52.6 |

Warm visits (the fourth, one per process here) do not show a difference either way: Donors and Stock cost about 45 % more after, Patients and Staff about 45 % less, the other five within 20 %, at the noise of a shared machine.
At 3.6 GHz, 100 Mcycles is about 28 ms. Of the two changes, measured separately in the owner's session before the isolation rule (4 fresh processes per build,
12 rounds): the side panels -5 to -33 % on first visits (Donors -33 %, Requests -27 %, Receipts -27 %, Patients -24 %, Stock -17 %, Appointments -11 %,
Staff -5 %) and 5 to 15 % on warm visits; PGO off a further 35 to 50 % on first visits and about half on second visits (4 fresh processes per setting, 6 rounds).

What is left of a first visit, after both changes, is up to 2.6 times a warm visit (Appointments; 0.8 to 2.6 over the nine screens): the theme's styles and templates are realized on the UI thread
the first time a screen uses them, and the code is not yet in its final tier. Every screen's first-visit gap is still 50 ms or more in this setting (50.4 to 114.9 ms).

## The harness, tonight (isolated desktop, both builds, alternating)

| Measurement (100x) | before, run 1 | after, run 1 | before, run 2 | after, run 2 |
|---|---|---|---|---|
| UI gap Dashboard | 47.9 | 47.4 | 33.0 | 96.5 |
| UI gap Donors | 2041.6 | 2040.6 | 66.5 | 2013.7 |
| UI gap Appointments | 70.1 | 198.8 | 37.8 | 66.7 |
| UI gap Patients | 50.0 | 57.9 | 32.7 | 55.7 |
| UI gap Requests | 47.1 | 112.0 | 48.0 | 38.0 |
| UI gap Stock | 33.4 | 76.0 | 41.6 | 37.7 |
| UI gap Receipts | 67.0 | 155.3 | 24.0 | 24.9 |
| UI gap Staff | 102.7 | 140.1 | 23.3 | 30.9 |
| UI gap Profile | 740.4 | 172.6 | 39.1 | 66.6 |
| Skeleton, worst of 10 visits, Donors / Stock / Staff | 293 / 393 / 195 | 283 / 448 / no skeleton on 1 visit | 249 / 182 / 123 | 129 / 50 / 40 |
| Cold dashboard, median of 7 | 2105.9 | 273.9 | 267.5 | 253.2 |
| Verdict | 19 missed | 23 missed | 10 missed, 10 missing (its API-call test recorded nothing) | 11 missed |

- The harness's gap test is the twelfth visit of each screen in its process (after the API-call test and the ten visits of the skeleton test): it measures
  warm visits, where the two builds cost about the same, and one sample per screen, which tonight's machine moved by tens of milliseconds between runs.
  These four runs do not show a difference between the builds, and do not show the budget met.
- Donors' 2-second gap is in both builds: one stall of the UI thread in that process on the isolated desktop, not a cost of the screen.
- Cold dashboard, compared on its own (`PerfPhase=cold`, 8 fresh processes per build, alternating, isolated): before median 328.1 ms
  (217.6 to 388.1), after 330.8 ms (234.9 to 442.9). Unchanged; over the 300 ms budget in that setting. The clock includes the test's own renders of the
  page into a bitmap, each of which is a large part of the time.

## Where the time goes (first visits)

- Almost all of a screen's gap is one dispatcher operation: the layout-and-render pass that builds the new view at navigation. On warm visits the gap is
  that pass plus 1 to 3 ms; the pass that applies the data, 250 ms later, takes 3 to 7 ms. It is CPU-bound (its cycles match its wall time).
- A sampled trace of the UI thread (dotnet-trace; coarse, and biased toward the runtime's safe points) splits that pass into template application (about
  60 %), measure (40 %), tree-change invalidation (25 %), BAML load (23 %) and text formatting (19 %), overlapping: no single hot spot.
- Tried and not kept: collapsing the table instead of hiding it while a screen loads (no gain); building every screen once on another thread first
  (no gain on first visits: they are per UI thread); the same with four rounds and PGO on (no gain on first visits, only on warm ones); building every
  screen on the same thread first (mixed).
- The dashboard sometimes gaps about 250 ms, in either build: while it loads, an animated render operation waits 50 to 65 ms per frame with the processor
  idle (frame pacing of a window that is not on a shown desktop), and input waits behind it. It came and went with the machine's state, not with the code.
- Found, not changed: `CrispBorder` draws its ring as a `CombinedGeometry`, whose bounds cost a geometry combine on every render (about 4 % of the pass).

## Earlier results

The 2.1 run of 2026-11-20 (`b0f4530`, cores 0-7) and the 2.0.1 comparison are in this file's history (`git log -p docs/perf/results.md`): UI gap 54 to
112 ms on all nine screens, cold dashboard 522 ms, both missed, 2.0.1 missing both too in the same machine state.

## The after build's harness run (run 2), as the harness wrote it

Produced by `tests/perf/run-perf.ps1 -Phases timing,1x,10x,100x` on 2026-11-21 02:33, every test process on the isolated desktop. 11 measurements missed,
2 test runs failed (the skeleton and gap tests, whose budgets they assert).

## Every measurement

| Budget | Dataset | Measurement | Statistic | Value | Limit | Verdict | Detail |
|---|---|---|---|---|---|---|---|
| lists | 1x | GET donors (page 1) | p95 of 40 warm requests | 5.2 ms | < 100 | met | median 3.6 ms, 10210 bytes |
| lists | 1x | GET donors search=a | p95 of 40 warm requests | 4.2 ms | < 100 | met | median 3.3 ms, 4443 bytes |
| lists | 1x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 5 ms | < 100 | met | median 3.7 ms, 7122 bytes |
| lists | 1x | GET donors page 60 (deep) | p95 of 40 warm requests | 3.5 ms | < 100 | met | median 2.9 ms, 48 bytes |
| lists | 1x | GET patients (page 1) | p95 of 40 warm requests | 4.5 ms | < 100 | met | median 3.3 ms, 4935 bytes |
| lists | 1x | GET patients search=a | p95 of 40 warm requests | 5.8 ms | < 100 | met | median 4.2 ms, 882 bytes |
| lists | 1x | GET patients page 60 (deep) | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.5 ms, 47 bytes |
| lists | 1x | GET appointments (page 1) | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 2.5 ms, 9754 bytes |
| lists | 1x | GET appointments status=Pending | p95 of 40 warm requests | 3.6 ms | < 100 | met | median 2.7 ms, 9753 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 4.9 ms | < 100 | met | median 3.1 ms, 9809 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 3.5 ms | < 100 | met | median 2.8 ms, 48 bytes |
| lists | 1x | GET appointments search=a, date range | p95 of 40 warm requests | 4.4 ms | < 100 | met | median 3.6 ms, 1022 bytes |
| lists | 1x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 7.9 ms | < 100 | met | median 5.3 ms, 47 bytes |
| lists | 1x | GET appointments page 500 (deep) | p95 of 40 warm requests | 3.8 ms | < 100 | met | median 3.0 ms, 49 bytes |
| lists | 1x | GET requests (page 1) | p95 of 40 warm requests | 3.3 ms | < 100 | met | median 2.7 ms, 8478 bytes |
| lists | 1x | GET requests status=Pending | p95 of 40 warm requests | 3.3 ms | < 100 | met | median 2.5 ms, 6778 bytes |
| lists | 1x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 3.2 ms | < 100 | met | median 2.2 ms, 6327 bytes |
| lists | 1x | GET requests search=a | p95 of 40 warm requests | 3.3 ms | < 100 | met | median 2.4 ms, 3929 bytes |
| lists | 1x | GET requests page 250 (deep) | p95 of 40 warm requests | 2.6 ms | < 100 | met | median 2.1 ms, 49 bytes |
| lists | 1x | GET receipts (page 1) | p95 of 40 warm requests | 3 ms | < 100 | met | median 2.4 ms, 9282 bytes |
| lists | 1x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 3.5 ms | < 100 | met | median 2.3 ms, 2661 bytes |
| lists | 1x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 14.9 ms | < 100 | met | median 7.4 ms, 9330 bytes |
| lists | 1x | GET receipts search=a | p95 of 40 warm requests | 17.3 ms | < 100 | met | median 6.2 ms, 9232 bytes |
| lists | 1x | GET receipts search=Ay | p95 of 40 warm requests | 4 ms | < 100 | met | median 3.0 ms, 45 bytes |
| lists | 1x | GET receipts page 100 | p95 of 40 warm requests | 2.5 ms | < 100 | met | median 1.8 ms, 49 bytes |
| lists | 1x | GET receipts page 500 (deep) | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.1 ms, 49 bytes |
| lists | 1x | GET staff pageSize=200 | p95 of 40 warm requests | 2.7 ms | < 100 | met | median 1.7 ms, 1600 bytes |
| lists | 1x | GET stock | p95 of 40 warm requests | 1.9 ms | < 100 | met | median 1.4 ms, 919 bytes |
| dashboard | 1x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 6.6 ms | < 100 | met | median 5.2 ms, 1710 bytes |
| staff-payload | 10x | GET staff pageSize=200 (110 staff) | size of one response | 15.5 KB | < 20 | met | 15909 bytes, 110 rows, longest name 19 chars, image bytes none |
| lists | 100x | GET donors (page 1) | p95 of 40 warm requests | 15.8 ms | < 100 | met | median 10.8 ms, 10291 bytes |
| lists | 100x | GET donors search=a | p95 of 40 warm requests | 30.4 ms | < 100 | met | median 21.7 ms, 10290 bytes |
| lists | 100x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 10.4 ms | < 100 | met | median 8.2 ms, 10432 bytes |
| lists | 100x | GET donors page 60 (deep) | p95 of 40 warm requests | 28.1 ms | < 100 | met | median 18.3 ms, 10430 bytes |
| lists | 100x | GET patients (page 1) | p95 of 40 warm requests | 32.3 ms | < 100 | met | median 19.9 ms, 7212 bytes |
| lists | 100x | GET patients search=a | p95 of 40 warm requests | 54.3 ms | < 100 | met | median 38.6 ms, 7211 bytes |
| lists | 100x | GET patients page 60 (deep) | p95 of 40 warm requests | 32.7 ms | < 100 | met | median 24.9 ms, 7105 bytes |
| lists | 100x | GET appointments (page 1) | p95 of 40 warm requests | 19.6 ms | < 100 | met | median 13.2 ms, 10065 bytes |
| lists | 100x | GET appointments status=Pending | p95 of 40 warm requests | 16.1 ms | < 100 | met | median 10.8 ms, 10064 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 12.9 ms | < 100 | met | median 10.3 ms, 10165 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 10.5 ms | < 100 | met | median 8.6 ms, 10068 bytes |
| lists | 100x | GET appointments search=a, date range | p95 of 40 warm requests | 5.4 ms | < 100 | met | median 4.4 ms, 10183 bytes |
| lists | 100x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 60.9 ms | < 100 | met | median 55.8 ms, 10083 bytes |
| lists | 100x | GET appointments page 500 (deep) | p95 of 40 warm requests | 11.5 ms | < 100 | met | median 9.6 ms, 10080 bytes |
| lists | 100x | GET requests (page 1) | p95 of 40 warm requests | 19.6 ms | < 100 | met | median 11.0 ms, 8677 bytes |
| lists | 100x | GET requests status=Pending | p95 of 40 warm requests | 24 ms | < 100 | met | median 12.6 ms, 8676 bytes |
| lists | 100x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 9.1 ms | < 100 | met | median 6.9 ms, 8653 bytes |
| lists | 100x | GET requests search=a | p95 of 40 warm requests | 34.2 ms | < 100 | met | median 27.0 ms, 8638 bytes |
| lists | 100x | GET requests page 250 (deep) | p95 of 40 warm requests | 29.1 ms | < 100 | met | median 14.5 ms, 8726 bytes |
| lists | 100x | GET receipts (page 1) | p95 of 40 warm requests | 21.8 ms | < 100 | met | median 9.5 ms, 9650 bytes |
| lists | 100x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 2.0 ms, 9787 bytes |
| lists | 100x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 11.6 ms | < 100 | met | median 8.7 ms, 9794 bytes |
| lists | 100x | GET receipts search=a | p95 of 40 warm requests | 190.2 ms | < 100 | **MISSED** | median 161.6 ms, 9719 bytes |
| lists | 100x | GET receipts search=Ay | p95 of 40 warm requests | 110.5 ms | < 100 | **MISSED** | median 80.9 ms, 9599 bytes |
| lists | 100x | GET receipts page 100 | p95 of 40 warm requests | 11.8 ms | < 100 | met | median 9.0 ms, 9657 bytes |
| lists | 100x | GET receipts page 500 (deep) | p95 of 40 warm requests | 12.1 ms | < 100 | met | median 9.7 ms, 9572 bytes |
| lists | 100x | GET staff pageSize=200 | p95 of 40 warm requests | 4.2 ms | < 100 | met | median 3.4 ms, 29074 bytes |
| lists | 100x | GET stock | p95 of 40 warm requests | 2 ms | < 100 | met | median 1.3 ms, 958 bytes |
| dashboard | 100x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 35.2 ms | < 100 | met | median 29.7 ms, 1435 bytes |
| api-calls | 100x | Dashboard: API calls for one screen load | count of one load | 4 calls | == 4, one per data set (dashboard, stock, appointments, requests) | met | GET /api/v1/dashboard, GET /api/v1/stock, GET /api/v1/appointments, GET /api/v1/requests; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Donors: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/donors; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Appointments: API calls for one screen load | count of one load | 2 calls | == 2, one per data set (appointments, appointments/views) | met | GET /api/v1/appointments, GET /api/v1/appointments/views; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Patients: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/patients; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Requests: API calls for one screen load | count of one load | 3 calls | == 3, one per data set (requests, requests/views, stock) | met | GET /api/v1/requests, GET /api/v1/requests/views, GET /api/v1/stock; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Stock: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/stock; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Receipts: API calls for one screen load | count of one load | 2 calls | == 2, one per data set (receipts, receipts/counts) | met | GET /api/v1/receipts, GET /api/v1/receipts/counts; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Staff: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/staff; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Profile: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/me; view models created 1; DataContext is the view model: True |
| api-calls | 100x | API calls started on the UI thread (all screens) | count over all loads | 0 calls | == 0 | met |  |
| grid | 100x | DataGrid with 10,000 items: row containers created | max of created and visible rows after scrolling to 25%, 50%, 99% | 14 rows | < 300 | met | 10000 items; created 14, in the visual tree 14; screens not using LifestreamDataGrid: none; views with a raw grid: none |
| skeleton | 100x | Skeleton after navigating to Dashboard | max of 10 visits (the first one is cold) | 41.4 ms | < 100 | met | first visit 23.1 ms, median 21.8 ms |
| skeleton | 100x | Skeleton after navigating to Donors | max of 10 visits (the first one is cold) | 129.3 ms | < 100 | **MISSED** | first visit 40.8 ms, median 55.5 ms |
| skeleton | 100x | Skeleton after navigating to Appointments | max of 10 visits (the first one is cold) | 103 ms | < 100 | **MISSED** | first visit 58.9 ms, median 49.5 ms |
| skeleton | 100x | Skeleton after navigating to Patients | max of 10 visits (the first one is cold) | 62.9 ms | < 100 | met | first visit 39.2 ms, median 33.9 ms |
| skeleton | 100x | Skeleton after navigating to Requests | max of 10 visits (the first one is cold) | 69 ms | < 100 | met | first visit 44.2 ms, median 44.2 ms |
| skeleton | 100x | Skeleton after navigating to Stock | max of 10 visits (the first one is cold) | 49.5 ms | < 100 | met | first visit 40.0 ms, median 39.7 ms |
| skeleton | 100x | Skeleton after navigating to Receipts | max of 10 visits (the first one is cold) | 62.3 ms | < 100 | met | first visit 59.3 ms, median 35.8 ms |
| skeleton | 100x | Skeleton after navigating to Staff | max of 10 visits (the first one is cold) | 40.3 ms | < 100 | met | first visit 36.5 ms, median 26.5 ms |
| skeleton | 100x | Skeleton after navigating to Profile | max of 10 visits (the first one is cold) | 146.5 ms | < 100 | **MISSED** | first visit 43.2 ms, median 43.8 ms |
| ui-gap | 100x | UI thread gap while loading Dashboard over 250 ms latency | largest wait of an input-priority post during one load | 96.5 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Donors over 250 ms latency | largest wait of an input-priority post during one load | 2013.7 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Appointments over 250 ms latency | largest wait of an input-priority post during one load | 66.7 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Patients over 250 ms latency | largest wait of an input-priority post during one load | 55.7 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Requests over 250 ms latency | largest wait of an input-priority post during one load | 38 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Stock over 250 ms latency | largest wait of an input-priority post during one load | 37.7 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Receipts over 250 ms latency | largest wait of an input-priority post during one load | 24.9 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Staff over 250 ms latency | largest wait of an input-priority post during one load | 30.9 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Profile over 250 ms latency | largest wait of an input-priority post during one load | 66.6 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap, worst of all screens | max over 9 loads | 2013.7 ms | < 50 | **MISSED** | worst on Donors |
| cold-dashboard | 100x | Dashboard first chart render after the data arrives, fresh process | median of 7 fresh processes | 253.2 ms | < 300 | met | runs (ms): 253.2, 273.9, 258.1, 251.4, 280.8, 242.7, 223.8; max 280.8 ms |

## Fixture databases

- **1x**: {"Staff":11,"Donor":100,"Patient":35,"Appointment":300,"Request":150,"Receipt":270,"BloodBag":275} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **10x**: {"Staff":110,"Donor":1000,"Patient":350,"Appointment":3000,"Request":1500,"Receipt":2700,"BloodBag":2662} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **100x**: {"Staff":1100,"Donor":10000,"Patient":3500,"Appointment":30000,"Request":15000,"Receipt":27000,"BloodBag":26937} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)

## How it is measured

- **Launch**: the published exe is started with `--perf-launch-pipe <name>`; the login window is created at (-20000, -20000), never activated and kept out of the task bar, writes one line to the named pipe when its first frame has rendered, and the process exits. The time is the script's stopwatch from just before process creation to that line. The foreground window is compared before and after every run.
- **API timings** are round trips over loopback HTTP from request sent to body read (the HTTP client and socket are included, so they are an upper bound of the server's own time), 8 warm-up requests and then 40 timed; p95 is the 38th of 40 sorted samples. The API is the real Release build in its own process over a real local SQL Server database; the dashboard cache is off.
- **Screen measurements** use the real client (real HTTP client, wake-aware handler, session, navigation, view models, views) hosted off-screen over a real socket to that API. The skeleton time runs from the navigation command until the dispatcher has finished the layout pass that puts the skeleton on screen (the final present to the screen adds at most one frame). The UI-thread gap is the longest an input-priority message posted from another thread waited for the UI thread while a screen loaded over a 250 ms-per-request link, first visits included. The API-call count includes retries and lazy requests that land within 400 ms of the load.
- **Cold dashboard**: a fresh process each time (`PerfPhase=cold`): warm-up as at app start, sign-in, then the dashboard is loaded; the clock starts when the response body is fully in memory and stops when a rendered bitmap of the page shows both chart series (each look renders the page, which is part of the time).
- Nothing is shown on the desktop and no focus is taken: every view is hosted in a window whose parent is a message-only window, and the launch window is off-screen and not activated.
- Every test process of this run ran on an isolated desktop (tests/isolated: a desktop of its own and a job object at below-normal priority, never shown, never given input), where WPF draws in software: absolute screen timings there differ from a GPU desktop, so compare runs made the same way.

