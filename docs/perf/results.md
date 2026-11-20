# Performance results (2.1)

Produced by `tests/perf/run-perf.ps1 -AssertBudgets` on 2026-11-20 05:55 (Pakistan Standard Time, UTC+05:00), at commit `b0f4530` of the 2.1 branch. The run exited 1. Budgets are design section 5.5 and are unchanged from 2.0; each is asserted with one stated statistic over a stated sample, never a best-of re-measure.

| | |
|---|---|
| CPU | AMD Ryzen 9 5900X 12-Core Processor (12 cores, 24 threads) |
| CPUs the run could use | **8 logical processors** (cores 0-7: processor affinity 0xFF, `DOTNET_PROCESSOR_COUNT=8`), normal priority. The 2.0 figures were taken on all 24 threads, so the two are not directly comparable. |
| Memory | 127.9 GB |
| OS | Microsoft Windows 11 Pro 10.0.26300 |
| .NET SDK | 11.0.100 |
| Power plan | Balanced |
| SQL Server | Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64) |
| Quiet check | CPU under 25% and no other registered process live before the run |

**Verdict: 8 of 10 budgets met, 2 missed.** Missed: the UI-thread gap during a load (all 9 screens, 54.1 to 112 ms against < 50 ms) and the first cold dashboard chart render (522.2 ms against < 300 ms). In the script's own words: 11 measurement(s) missed, 0 expected measurement(s) missing, 2 test run(s) failed (the two test runs are the ones that assert these two budgets: the fake-API cold dashboard test measured 562 ms, and the real-API UI-thread gap test).

The client and desktop code changed after this run (the touch-up merge `ddb502b`: 34 files, 13 of them XAML views); the API code did not. 2.1.0 as released was not measured again.

## Summary

| Budget | Limit | Measured | Statistic | Verdict |
|---|---|---|---|---|
| App launch to login window (warm OS cache, Release, self-contained ReadyToRun) | < 1.0 s | 803.1 ms | median of 10 cold-process runs after 1 warm-up | met |
| Published app started from another folder (temp) reaches the login window | 0 failures | 0 failures | one run | met |
| Any screen: skeleton visible after navigation | < 100 ms | worst 54 ms over 9 measurement(s) | max of 10 visits (the first one is cold) | met |
| Warm API list endpoints (local SQL Server) | p95 < 100 ms | worst 45.3 ms over 56 measurement(s) | p95 of 40 warm requests | met |
| Dashboard endpoint, no cache | p95 < 100 ms | worst 21.4 ms over 2 measurement(s) | p95 of 40 warm requests | met |
| API calls per screen load | exactly 1 per data set | 10 screens/checks, 0 off | count of one load | met |
| UI thread gap during a load over a throttled link (250 ms latency) | < 50 ms | worst 112 ms over 10 measurement(s) | largest wait of an input-priority post during one load | **MISSED** (all 9 screens and the worst-of line) |
| Staff list payload for 110 staff | < 20 KB, no image bytes | 15.5 KB | size of one response | met |
| Grid with 10,000 items | < 300 row containers | 14 rows | max of created and visible rows after scrolling to 25%, 50%, 99% | met |
| First (cold, fresh process) dashboard chart render after data arrives | < 300 ms | 522.2 ms | median of 7 fresh processes | **MISSED** |

## The two misses in context

**2.0.1 on the same machine state misses both budgets too.** 2.0.1 (`d916671`) and this branch (`b0f4530`) were measured back to back on 8 logical processors (cores 0-7) on a quiet machine, both at low priority, with the same harness (the `screens` phase and 7 cold dashboards). That pair of runs is the like-for-like comparison; it was taken at a different priority from the main run above, and its branch figures differ from it.

| | 2.0.1 | 2.1 branch | Limit |
|---|---|---|---|
| UI-thread gap, Dashboard | 112.9 ms | 157.4 ms | < 50 |
| UI-thread gap, Donors | 110.0 ms | 95.8 ms | < 50 |
| UI-thread gap, Appointments | 26.3 ms | 78.8 ms | < 50 |
| UI-thread gap, Patients | 18.4 ms | 60.6 ms | < 50 |
| UI-thread gap, Requests | 23.4 ms | 70.3 ms | < 50 |
| UI-thread gap, Stock | 15.8 ms | 52.6 ms | < 50 |
| UI-thread gap, Receipts | 33.6 ms | 39.1 ms | < 50 |
| UI-thread gap, Staff | 22.5 ms | 45.7 ms | < 50 |
| UI-thread gap, Profile | 14.3 ms | 55.9 ms | < 50 |
| Cold dashboard, median of 7 | 612.9 ms | 538.5 ms | < 300 |

- **Cold dashboard**: not a regression. Both versions miss it on 8 processors, and 2.1 is the faster of the two. (2.0's 186.6 ms was measured on all 24 threads.)
- **UI-thread gap**: partly a regression. Dashboard and Donors are over the limit on both versions; Appointments, Patients, Requests, Stock and Profile are over it on 2.1 only (2.0.1: 14 to 34 ms on the seven other screens). Some of the difference is test order: the tests run in a different order in the two versions, so the gap test follows a different number of earlier navigations in the same process.
- **Cause, measured with a temporary diagnostic** (every dispatcher operation of 2 ms or more during each screen's first three loads; not part of the product): on the eight list and form screens the gap is one WPF layout-and-render pass at the moment of navigation, which builds the new screen's view. No API call, chart or data work is in it; the later pass that applies the data takes 3 to 7 ms. Building one redesigned screen off-screen with no data takes 30 to 53 ms (XAML load, measure, arrange) for 200 to 300 visuals, with no single hot spot. Visit for visit, the redesigned screens cost about 1.3 to 2.5 times as much to build as 2.0's, and a first visit costs 2 to 4 times a later one on both versions. The dashboard is different and the same on both versions: no single long operation, but input-priority work waits while the skeleton animates and the chart ticks until the data lands.
- **Follow-up work, not done in 2.1**: making the screens cheaper to build (for example building the closed side panel and the editor host on first use, and lighter toolbars). A start-up warm-up of the views was tried and not shipped: it improved the test's numbers but not a user's first visit. The budgets are unchanged.

## Every measurement

| Budget | Dataset | Measurement | Statistic | Value | Limit | Verdict | Detail |
|---|---|---|---|---|---|---|---|
| launch-cwd | n/a | Published exe started from the temp folder reaches the login window | one run | 0 failures | == 0 | met |  |
| launch | n/a | App launch to login window rendered (self-contained ReadyToRun, Release) | median of 10 cold-process runs after 1 warm-up | 803.1 ms | < 1000 | met | runs (ms): 789, 790, 795, 798, 800, 806, 809, 817, 829, 834; in-app time from process start to first frame, median 803 ms; foreground window changed in 0 of 10 runs; window created at -20000,-20000 not activated, not in the task bar |
| lists | 1x | GET donors (page 1) | p95 of 40 warm requests | 2.8 ms | < 100 | met | median 2.2 ms, 8151 bytes |
| lists | 1x | GET donors search=a | p95 of 40 warm requests | 8.6 ms | < 100 | met | median 7.6 ms, 1456 bytes |
| lists | 1x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 2.1 ms | < 100 | met | median 1.7 ms, 2678 bytes |
| lists | 1x | GET donors page 60 (deep) | p95 of 40 warm requests | 2.3 ms | < 100 | met | median 1.8 ms, 47 bytes |
| lists | 1x | GET patients (page 1) | p95 of 40 warm requests | 2.5 ms | < 100 | met | median 2.2 ms, 4924 bytes |
| lists | 1x | GET patients search=a | p95 of 40 warm requests | 9.7 ms | < 100 | met | median 8.7 ms, 739 bytes |
| lists | 1x | GET patients page 60 (deep) | p95 of 40 warm requests | 2 ms | < 100 | met | median 1.7 ms, 47 bytes |
| lists | 1x | GET appointments (page 1) | p95 of 40 warm requests | 2.5 ms | < 100 | met | median 2.0 ms, 9789 bytes |
| lists | 1x | GET appointments status=Pending | p95 of 40 warm requests | 2.7 ms | < 100 | met | median 2.1 ms, 9788 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 2.7 ms | < 100 | met | median 2.1 ms, 9791 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 2.4 ms | < 100 | met | median 2.0 ms, 48 bytes |
| lists | 1x | GET appointments search=a, date range | p95 of 40 warm requests | 12.2 ms | < 100 | met | median 11.4 ms, 631 bytes |
| lists | 1x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 12.9 ms | < 100 | met | median 11.5 ms, 47 bytes |
| lists | 1x | GET appointments page 500 (deep) | p95 of 40 warm requests | 2.4 ms | < 100 | met | median 1.9 ms, 49 bytes |
| lists | 1x | GET requests (page 1) | p95 of 40 warm requests | 1.9 ms | < 100 | met | median 1.6 ms, 8498 bytes |
| lists | 1x | GET requests status=Pending | p95 of 40 warm requests | 2.5 ms | < 100 | met | median 1.7 ms, 6793 bytes |
| lists | 1x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 2.2 ms | < 100 | met | median 1.7 ms, 5155 bytes |
| lists | 1x | GET requests search=a | p95 of 40 warm requests | 11.1 ms | < 100 | met | median 9.4 ms, 3431 bytes |
| lists | 1x | GET requests page 250 (deep) | p95 of 40 warm requests | 2.2 ms | < 100 | met | median 1.9 ms, 49 bytes |
| lists | 1x | GET receipts (page 1) | p95 of 40 warm requests | 2.9 ms | < 100 | met | median 2.2 ms, 9240 bytes |
| lists | 1x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 2.3 ms | < 100 | met | median 1.7 ms, 2633 bytes |
| lists | 1x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 3.2 ms | < 100 | met | median 2.2 ms, 9313 bytes |
| lists | 1x | GET receipts search=a | p95 of 40 warm requests | 5 ms | < 100 | met | median 3.8 ms, 9271 bytes |
| lists | 1x | GET receipts search=Ay | p95 of 40 warm requests | 3.5 ms | < 100 | met | median 2.5 ms, 939 bytes |
| lists | 1x | GET receipts page 100 | p95 of 40 warm requests | 2 ms | < 100 | met | median 1.4 ms, 49 bytes |
| lists | 1x | GET receipts page 500 (deep) | p95 of 40 warm requests | 2.3 ms | < 100 | met | median 1.5 ms, 49 bytes |
| lists | 1x | GET staff pageSize=200 | p95 of 40 warm requests | 1.9 ms | < 100 | met | median 1.4 ms, 1600 bytes |
| lists | 1x | GET stock | p95 of 40 warm requests | 1.4 ms | < 100 | met | median 1.2 ms, 917 bytes |
| dashboard | 1x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 6.2 ms | < 100 | met | median 4.6 ms, 1798 bytes |
| staff-payload | 10x | GET staff pageSize=200 (110 staff) | size of one response | 15.5 KB | < 20 | met | 15909 bytes, 110 rows, longest name 19 chars, image bytes none |
| lists | 100x | GET donors (page 1) | p95 of 40 warm requests | 5 ms | < 100 | met | median 4.1 ms, 10377 bytes |
| lists | 100x | GET donors search=a | p95 of 40 warm requests | 11.3 ms | < 100 | met | median 9.3 ms, 10376 bytes |
| lists | 100x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 3.8 ms | < 100 | met | median 3.0 ms, 10274 bytes |
| lists | 100x | GET donors page 60 (deep) | p95 of 40 warm requests | 10 ms | < 100 | met | median 6.8 ms, 10278 bytes |
| lists | 100x | GET patients (page 1) | p95 of 40 warm requests | 16.8 ms | < 100 | met | median 12.6 ms, 7198 bytes |
| lists | 100x | GET patients search=a | p95 of 40 warm requests | 20.5 ms | < 100 | met | median 18.0 ms, 7197 bytes |
| lists | 100x | GET patients page 60 (deep) | p95 of 40 warm requests | 14.4 ms | < 100 | met | median 10.5 ms, 7126 bytes |
| lists | 100x | GET appointments (page 1) | p95 of 40 warm requests | 9.6 ms | < 100 | met | median 8.1 ms, 9979 bytes |
| lists | 100x | GET appointments status=Pending | p95 of 40 warm requests | 8.6 ms | < 100 | met | median 7.0 ms, 9978 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 8.5 ms | < 100 | met | median 7.1 ms, 10035 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 10.5 ms | < 100 | met | median 8.1 ms, 10025 bytes |
| lists | 100x | GET appointments search=a, date range | p95 of 40 warm requests | 20.3 ms | < 100 | met | median 18.4 ms, 9627 bytes |
| lists | 100x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 27.6 ms | < 100 | met | median 25.0 ms, 10006 bytes |
| lists | 100x | GET appointments page 500 (deep) | p95 of 40 warm requests | 12.2 ms | < 100 | met | median 10.5 ms, 10028 bytes |
| lists | 100x | GET requests (page 1) | p95 of 40 warm requests | 8.1 ms | < 100 | met | median 7.1 ms, 8666 bytes |
| lists | 100x | GET requests status=Pending | p95 of 40 warm requests | 8.4 ms | < 100 | met | median 6.1 ms, 8665 bytes |
| lists | 100x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 8.2 ms | < 100 | met | median 6.5 ms, 8654 bytes |
| lists | 100x | GET requests search=a | p95 of 40 warm requests | 20.4 ms | < 100 | met | median 18.3 ms, 8617 bytes |
| lists | 100x | GET requests page 250 (deep) | p95 of 40 warm requests | 8.1 ms | < 100 | met | median 6.7 ms, 8755 bytes |
| lists | 100x | GET receipts (page 1) | p95 of 40 warm requests | 5.8 ms | < 100 | met | median 4.6 ms, 9575 bytes |
| lists | 100x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 1.9 ms | < 100 | met | median 1.4 ms, 9696 bytes |
| lists | 100x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 5.6 ms | < 100 | met | median 4.4 ms, 9663 bytes |
| lists | 100x | GET receipts search=a | p95 of 40 warm requests | 45.3 ms | < 100 | met | median 42.9 ms, 9518 bytes |
| lists | 100x | GET receipts search=Ay | p95 of 40 warm requests | 36.5 ms | < 100 | met | median 31.9 ms, 9564 bytes |
| lists | 100x | GET receipts page 100 | p95 of 40 warm requests | 5.9 ms | < 100 | met | median 4.8 ms, 9524 bytes |
| lists | 100x | GET receipts page 500 (deep) | p95 of 40 warm requests | 7.2 ms | < 100 | met | median 6.5 ms, 9594 bytes |
| lists | 100x | GET staff pageSize=200 | p95 of 40 warm requests | 3.5 ms | < 100 | met | median 2.6 ms, 29074 bytes |
| lists | 100x | GET stock | p95 of 40 warm requests | 1.3 ms | < 100 | met | median 1.0 ms, 953 bytes |
| dashboard | 100x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 21.4 ms | < 100 | met | median 19.1 ms, 1428 bytes |
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
| ui-gap | 100x | UI thread gap while loading Dashboard over 250 ms latency | largest wait of an input-priority post during one load | 112 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Donors over 250 ms latency | largest wait of an input-priority post during one load | 109.7 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Appointments over 250 ms latency | largest wait of an input-priority post during one load | 94 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Patients over 250 ms latency | largest wait of an input-priority post during one load | 65.4 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Requests over 250 ms latency | largest wait of an input-priority post during one load | 69.1 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Stock over 250 ms latency | largest wait of an input-priority post during one load | 67.6 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Receipts over 250 ms latency | largest wait of an input-priority post during one load | 58.5 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Staff over 250 ms latency | largest wait of an input-priority post during one load | 54.1 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap while loading Profile over 250 ms latency | largest wait of an input-priority post during one load | 63.6 ms | < 50 | **MISSED** |  |
| ui-gap | 100x | UI thread gap, worst of all screens | max over 9 loads | 112 ms | < 50 | **MISSED** | worst on Dashboard |
| skeleton | 100x | Skeleton after navigating to Dashboard | max of 10 visits (the first one is cold) | 35.8 ms | < 100 | met | first visit 29.7 ms, median 22.5 ms |
| skeleton | 100x | Skeleton after navigating to Donors | max of 10 visits (the first one is cold) | 45.7 ms | < 100 | met | first visit 45.1 ms, median 37.3 ms |
| skeleton | 100x | Skeleton after navigating to Appointments | max of 10 visits (the first one is cold) | 54 ms | < 100 | met | first visit 29.9 ms, median 29.5 ms |
| skeleton | 100x | Skeleton after navigating to Patients | max of 10 visits (the first one is cold) | 34 ms | < 100 | met | first visit 22.1 ms, median 21.5 ms |
| skeleton | 100x | Skeleton after navigating to Requests | max of 10 visits (the first one is cold) | 45.3 ms | < 100 | met | first visit 28.9 ms, median 30.3 ms |
| skeleton | 100x | Skeleton after navigating to Stock | max of 10 visits (the first one is cold) | 38.2 ms | < 100 | met | first visit 38.2 ms, median 27.7 ms |
| skeleton | 100x | Skeleton after navigating to Receipts | max of 10 visits (the first one is cold) | 36.5 ms | < 100 | met | first visit 19.7 ms, median 20.5 ms |
| skeleton | 100x | Skeleton after navigating to Staff | max of 10 visits (the first one is cold) | 33.4 ms | < 100 | met | first visit 22.2 ms, median 20.7 ms |
| skeleton | 100x | Skeleton after navigating to Profile | max of 10 visits (the first one is cold) | 44.1 ms | < 100 | met | first visit 44.1 ms, median 30.3 ms |
| grid | 100x | DataGrid with 10,000 items: row containers created | max of created and visible rows after scrolling to 25%, 50%, 99% | 14 rows | < 300 | met | 10000 items; created 14, in the visual tree 14; screens not using LifestreamDataGrid: none; views with a raw grid: none |
| cold-dashboard | 100x | Dashboard first chart render after the data arrives, fresh process | median of 7 fresh processes | 522.2 ms | < 300 | **MISSED** | runs (ms): 545.5, 338.2, 519.5, 522.2, 546.2, 528, 358.1; max 546.2 ms |

## Fixture databases

- **1x**: {"Staff":11,"Donor":40,"Patient":35,"Appointment":300,"Request":150,"Receipt":270,"BloodBag":275} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **10x**: {"Staff":110,"Donor":400,"Patient":350,"Appointment":3000,"Request":1500,"Receipt":2700,"BloodBag":2695} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **100x**: {"Staff":1100,"Donor":4000,"Patient":3500,"Appointment":30000,"Request":15000,"Receipt":27000,"BloodBag":26962} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)

## How it is measured

- **Launch**: the published exe is started with `--perf-launch-pipe <name>`; the login window is created at (-20000, -20000), never activated and kept out of the task bar, writes one line to the named pipe when its first frame has rendered, and the process exits. The time is the script's stopwatch from just before process creation to that line. The foreground window is compared before and after every run.
- **API timings** are round trips over loopback HTTP from request sent to body read (the HTTP client and socket are included, so they are an upper bound of the server's own time), 8 warm-up requests and then 40 timed; p95 is the 38th of 40 sorted samples. The API is the real Release build in its own process over a real local SQL Server database; the dashboard cache is off.
- **Screen measurements** use the real client (real HTTP client, wake-aware handler, session, navigation, view models, views) hosted off-screen over a real socket to that API. The skeleton time runs from the navigation command until the dispatcher has finished the layout pass that puts the skeleton on screen (the final present to the screen adds at most one frame). The UI-thread gap is the longest an input-priority message posted from another thread waited for the UI thread while a screen loaded over a 250 ms-per-request link, first visits included. The API-call count includes retries and lazy requests that land within 400 ms of the load.
- **Cold dashboard**: a fresh process each time (`PerfPhase=cold`): warm-up as at app start, sign-in, then the dashboard is loaded; the clock starts when the response body is fully in memory and stops when a rendered bitmap of the page shows both chart series (each look renders the page, which is part of the time).
- Nothing is shown on the desktop and no focus is taken: every view is hosted in a window whose parent is a message-only window, and the launch window is off-screen and not activated.
