# Performance results

Produced by `tests/perf/run-perf.ps1 -AssertBudgets` on 2026-11-19 09:04 (Pakistan Standard Time, UTC+05:00). Budgets are design section 5.5; each is asserted with one stated statistic over a stated sample, never a best-of re-measure.

| | |
|---|---|
| CPU | AMD Ryzen 9 5900X 12-Core Processor (12 cores, 24 threads) |
| Memory | 127.9 GB |
| OS | Microsoft Windows 11 Pro 10.0.26300 |
| .NET SDK | 11.0.100 |
| Power plan | Balanced |
| SQL Server | Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64) |
| Quiet check | CPU under 25% and no other registered process live before the run |

**Verdict: every budget met**

## Summary

| Budget | Limit | Measured | Statistic | Verdict |
|---|---|---|---|---|
| App launch to login window (warm OS cache, Release, self-contained ReadyToRun) | < 1.0 s | 663.7 ms | median of 10 cold-process runs after 1 warm-up | met |
| Any screen: skeleton visible after navigation | < 100 ms | worst 84.4 ms over 9 measurement(s) | max of 10 visits (the first one is cold) | met |
| Warm API list endpoints (local SQL Server) | p95 < 100 ms | worst 79 ms over 56 measurement(s) | p95 of 40 warm requests | met |
| Dashboard endpoint, no cache | p95 < 100 ms | worst 31.3 ms over 2 measurement(s) | p95 of 40 warm requests | met |
| API calls per screen load | exactly 1 per data set | 10 screens/checks, 0 off | count of one load | met |
| UI thread gap during a load over a throttled link (250 ms latency) | < 50 ms | worst 39.7 ms over 10 measurement(s) | largest wait of an input-priority post during one load | met |
| Staff list payload for 110 staff | < 20 KB, no image bytes | 15.6 KB | size of one response | met |
| Grid with 10,000 items | < 300 row containers | 15 rows | max of created and visible rows after scrolling to 25%, 50%, 99% | met |
| First (cold, fresh process) dashboard chart render after data arrives | < 300 ms | 233.8 ms | median of 7 fresh processes | met |

## Every measurement

| Budget | Dataset | Measurement | Statistic | Value | Limit | Verdict | Detail |
|---|---|---|---|---|---|---|---|
| launch | n/a | App launch to login window rendered (self-contained ReadyToRun, Release) | median of 10 cold-process runs after 1 warm-up | 663.7 ms | < 1000 | met | runs (ms): 641, 643, 643, 654, 659, 668, 668, 691, 712, 734; in-app time from process start to first frame, median 664 ms; foreground window changed in 0 of 10 runs; window created at -20000,-20000 not activated, not in the task bar |
| lists | 1x | GET donors (page 1) | p95 of 40 warm requests | 3.6 ms | < 100 | met | median 2.3 ms, 7610 bytes |
| lists | 1x | GET donors search=a | p95 of 40 warm requests | 8 ms | < 100 | met | median 5.6 ms, 611 bytes |
| lists | 1x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 3 ms | < 100 | met | median 2.3 ms, 1936 bytes |
| lists | 1x | GET donors page 60 (deep) | p95 of 40 warm requests | 3 ms | < 100 | met | median 2.6 ms, 47 bytes |
| lists | 1x | GET patients (page 1) | p95 of 40 warm requests | 3.9 ms | < 100 | met | median 3.2 ms, 4941 bytes |
| lists | 1x | GET patients search=a | p95 of 40 warm requests | 12 ms | < 100 | met | median 10.6 ms, 318 bytes |
| lists | 1x | GET patients page 60 (deep) | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 2.7 ms, 47 bytes |
| lists | 1x | GET appointments (page 1) | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 3.0 ms, 8634 bytes |
| lists | 1x | GET appointments status=Pending | p95 of 40 warm requests | 3.6 ms | < 100 | met | median 3.0 ms, 8633 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 4.2 ms | < 100 | met | median 3.3 ms, 8154 bytes |
| lists | 1x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 4.8 ms | < 100 | met | median 3.5 ms, 48 bytes |
| lists | 1x | GET appointments search=a, date range | p95 of 40 warm requests | 21.4 ms | < 100 | met | median 13.6 ms, 903 bytes |
| lists | 1x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 14.8 ms | < 100 | met | median 12.6 ms, 47 bytes |
| lists | 1x | GET appointments page 500 (deep) | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 2.9 ms, 49 bytes |
| lists | 1x | GET requests (page 1) | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.3 ms, 8510 bytes |
| lists | 1x | GET requests status=Pending | p95 of 40 warm requests | 3.3 ms | < 100 | met | median 2.3 ms, 6812 bytes |
| lists | 1x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 3 ms | < 100 | met | median 2.2 ms, 7010 bytes |
| lists | 1x | GET requests search=a | p95 of 40 warm requests | 10.6 ms | < 100 | met | median 9.3 ms, 1216 bytes |
| lists | 1x | GET requests page 250 (deep) | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.0 ms, 49 bytes |
| lists | 1x | GET receipts (page 1) | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.5 ms, 9246 bytes |
| lists | 1x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 3.4 ms | < 100 | met | median 2.4 ms, 8436 bytes |
| lists | 1x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 3.1 ms | < 100 | met | median 2.4 ms, 8789 bytes |
| lists | 1x | GET receipts search=a | p95 of 40 warm requests | 5 ms | < 100 | met | median 4.0 ms, 8789 bytes |
| lists | 1x | GET receipts search=Ay | p95 of 40 warm requests | 3.7 ms | < 100 | met | median 2.6 ms, 578 bytes |
| lists | 1x | GET receipts page 100 | p95 of 40 warm requests | 2.4 ms | < 100 | met | median 1.9 ms, 49 bytes |
| lists | 1x | GET receipts page 500 (deep) | p95 of 40 warm requests | 2.2 ms | < 100 | met | median 1.7 ms, 49 bytes |
| lists | 1x | GET staff pageSize=200 | p95 of 40 warm requests | 2.5 ms | < 100 | met | median 1.9 ms, 1615 bytes |
| lists | 1x | GET stock | p95 of 40 warm requests | 2 ms | < 100 | met | median 1.7 ms, 719 bytes |
| dashboard | 1x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 5.4 ms | < 100 | met | median 4.6 ms, 1719 bytes |
| staff-payload | 10x | GET staff pageSize=200 (110 staff) | size of one response | 15.6 KB | < 20 | met | 15959 bytes, 110 rows, longest name 21 chars, image bytes none |
| lists | 100x | GET donors (page 1) | p95 of 40 warm requests | 11.6 ms | < 100 | met | median 8.3 ms, 9634 bytes |
| lists | 100x | GET donors search=a | p95 of 40 warm requests | 8.3 ms | < 100 | met | median 7.3 ms, 9633 bytes |
| lists | 100x | GET donors bloodType=OPos sort=-lastDonated | p95 of 40 warm requests | 5.4 ms | < 100 | met | median 4.2 ms, 9568 bytes |
| lists | 100x | GET donors page 60 (deep) | p95 of 40 warm requests | 11 ms | < 100 | met | median 8.9 ms, 9586 bytes |
| lists | 100x | GET patients (page 1) | p95 of 40 warm requests | 22.1 ms | < 100 | met | median 18.0 ms, 7206 bytes |
| lists | 100x | GET patients search=a | p95 of 40 warm requests | 37.9 ms | < 100 | met | median 25.8 ms, 7205 bytes |
| lists | 100x | GET patients page 60 (deep) | p95 of 40 warm requests | 24.2 ms | < 100 | met | median 20.2 ms, 7128 bytes |
| lists | 100x | GET appointments (page 1) | p95 of 40 warm requests | 11.1 ms | < 100 | met | median 9.8 ms, 8832 bytes |
| lists | 100x | GET appointments status=Pending | p95 of 40 warm requests | 10.5 ms | < 100 | met | median 8.2 ms, 8831 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos | p95 of 40 warm requests | 9.3 ms | < 100 | met | median 7.8 ms, 8885 bytes |
| lists | 100x | GET appointments status=Completed bloodType=OPos sort=-placed page 100 | p95 of 40 warm requests | 11 ms | < 100 | met | median 9.4 ms, 8884 bytes |
| lists | 100x | GET appointments search=a, date range | p95 of 40 warm requests | 21.2 ms | < 100 | met | median 18.8 ms, 8505 bytes |
| lists | 100x | GET appointments search=a sort=slot page 20 | p95 of 40 warm requests | 33.5 ms | < 100 | met | median 27.9 ms, 8888 bytes |
| lists | 100x | GET appointments page 500 (deep) | p95 of 40 warm requests | 9.4 ms | < 100 | met | median 7.1 ms, 8900 bytes |
| lists | 100x | GET requests (page 1) | p95 of 40 warm requests | 11.8 ms | < 100 | met | median 8.6 ms, 8669 bytes |
| lists | 100x | GET requests status=Pending | p95 of 40 warm requests | 11.4 ms | < 100 | met | median 8.7 ms, 8668 bytes |
| lists | 100x | GET requests status=Fulfilled bloodType=OPos sort=requiredBy | p95 of 40 warm requests | 11.8 ms | < 100 | met | median 9.3 ms, 8644 bytes |
| lists | 100x | GET requests search=a | p95 of 40 warm requests | 26.7 ms | < 100 | met | median 23.2 ms, 8643 bytes |
| lists | 100x | GET requests page 250 (deep) | p95 of 40 warm requests | 12.4 ms | < 100 | met | median 10.2 ms, 8694 bytes |
| lists | 100x | GET receipts (page 1) | p95 of 40 warm requests | 7.3 ms | < 100 | met | median 5.9 ms, 9525 bytes |
| lists | 100x | GET receipts kind=Donation, date range | p95 of 40 warm requests | 2.2 ms | < 100 | met | median 1.4 ms, 9677 bytes |
| lists | 100x | GET receipts kind=Donation bloodType=OPos | p95 of 40 warm requests | 8.9 ms | < 100 | met | median 6.9 ms, 9632 bytes |
| lists | 100x | GET receipts search=a | p95 of 40 warm requests | 79 ms | < 100 | met | median 57.9 ms, 9578 bytes |
| lists | 100x | GET receipts search=Ay | p95 of 40 warm requests | 38.9 ms | < 100 | met | median 34.1 ms, 9549 bytes |
| lists | 100x | GET receipts page 100 | p95 of 40 warm requests | 7.6 ms | < 100 | met | median 5.7 ms, 9476 bytes |
| lists | 100x | GET receipts page 500 (deep) | p95 of 40 warm requests | 12.3 ms | < 100 | met | median 8.7 ms, 9516 bytes |
| lists | 100x | GET staff pageSize=200 | p95 of 40 warm requests | 4.2 ms | < 100 | met | median 3.1 ms, 29117 bytes |
| lists | 100x | GET stock | p95 of 40 warm requests | 3.7 ms | < 100 | met | median 3.1 ms, 735 bytes |
| dashboard | 100x | GET dashboard (default 12 months, no cache) | p95 of 40 warm requests | 31.3 ms | < 100 | met | median 23.8 ms, 1429 bytes |
| skeleton | 100x | Skeleton after navigating to Dashboard | max of 10 visits (the first one is cold) | 55.6 ms | < 100 | met | first visit 53.6 ms, median 40.5 ms |
| skeleton | 100x | Skeleton after navigating to Donors | max of 10 visits (the first one is cold) | 84.4 ms | < 100 | met | first visit 81.5 ms, median 66.2 ms |
| skeleton | 100x | Skeleton after navigating to Appointments | max of 10 visits (the first one is cold) | 61.5 ms | < 100 | met | first visit 61.5 ms, median 41.4 ms |
| skeleton | 100x | Skeleton after navigating to Patients | max of 10 visits (the first one is cold) | 39.9 ms | < 100 | met | first visit 26.9 ms, median 25.1 ms |
| skeleton | 100x | Skeleton after navigating to Requests | max of 10 visits (the first one is cold) | 50.9 ms | < 100 | met | first visit 23.4 ms, median 18.9 ms |
| skeleton | 100x | Skeleton after navigating to Stock | max of 10 visits (the first one is cold) | 15.9 ms | < 100 | met | first visit 12.1 ms, median 8.9 ms |
| skeleton | 100x | Skeleton after navigating to Receipts | max of 10 visits (the first one is cold) | 36.3 ms | < 100 | met | first visit 18.2 ms, median 21.8 ms |
| skeleton | 100x | Skeleton after navigating to Staff | max of 10 visits (the first one is cold) | 26.7 ms | < 100 | met | first visit 20.4 ms, median 20.1 ms |
| skeleton | 100x | Skeleton after navigating to Profile | max of 10 visits (the first one is cold) | 26.7 ms | < 100 | met | first visit 19.8 ms, median 14.8 ms |
| api-calls | 100x | Dashboard: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/dashboard; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Donors: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/donors; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Appointments: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/appointments; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Patients: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/patients; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Requests: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/requests; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Stock: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/stock; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Receipts: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/receipts; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Staff: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/staff; view models created 1; DataContext is the view model: True |
| api-calls | 100x | Profile: API calls for one screen load | count of one load | 1 calls | == 1 | met | GET /api/v1/me; view models created 1; DataContext is the view model: True |
| api-calls | 100x | API calls started on the UI thread (all screens) | count over all loads | 0 calls | == 0 | met |  |
| grid | 100x | DataGrid with 10,000 items: row containers created | max of created and visible rows after scrolling to 25%, 50%, 99% | 15 rows | < 300 | met | 10000 items; created 15, in the visual tree 15; screens not using LifestreamDataGrid: none; views with a raw grid: none |
| ui-gap | 100x | UI thread gap while loading Dashboard over 250 ms latency | largest wait of an input-priority post during one load | 33.2 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Donors over 250 ms latency | largest wait of an input-priority post during one load | 33.6 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Appointments over 250 ms latency | largest wait of an input-priority post during one load | 32 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Patients over 250 ms latency | largest wait of an input-priority post during one load | 26.7 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Requests over 250 ms latency | largest wait of an input-priority post during one load | 29.5 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Stock over 250 ms latency | largest wait of an input-priority post during one load | 39.7 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Receipts over 250 ms latency | largest wait of an input-priority post during one load | 23.4 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Staff over 250 ms latency | largest wait of an input-priority post during one load | 24.1 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap while loading Profile over 250 ms latency | largest wait of an input-priority post during one load | 17.2 ms | < 50 | met |  |
| ui-gap | 100x | UI thread gap, worst of all screens | max over 9 loads | 39.7 ms | < 50 | met | worst on Stock |
| cold-dashboard | 100x | Dashboard first chart render after the data arrives, fresh process | median of 7 fresh processes | 233.8 ms | < 300 | met | runs (ms): 230.3, 284, 233.8, 254.5, 210, 188.5, 262.4; max 284 ms |

## Fixture databases

- **1x**: {"Staff":11,"Donor":40,"Patient":35,"Appointment":300,"Request":150,"Receipt":270,"BloodBag":270} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **10x**: {"Staff":110,"Donor":400,"Patient":350,"Appointment":3000,"Request":1500,"Receipt":2700,"BloodBag":2686} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)
- **100x**: {"Staff":1100,"Donor":4000,"Patient":3500,"Appointment":30000,"Request":15000,"Receipt":27000,"BloodBag":26948} (Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64); admin session user is a generated administrator; index rebuild and full-scan statistics done)

## How it is measured

- **Launch**: the published exe is started with `--perf-launch-pipe <name>`; the login window is created at (-20000, -20000), never activated and kept out of the task bar, writes one line to the named pipe when its first frame has rendered, and the process exits. The time is the script's stopwatch from just before process creation to that line. The foreground window is compared before and after every run.
- **API timings** are round trips over loopback HTTP from request sent to body read (the HTTP client and socket are included, so they are an upper bound of the server's own time), 8 warm-up requests and then 40 timed; p95 is the 38th of 40 sorted samples. The API is the real Release build in its own process over a real local SQL Server database; the dashboard cache is off.
- **Screen measurements** use the real client (real HTTP client, wake-aware handler, session, navigation, view models, views) hosted off-screen over a real socket to that API. The skeleton time runs from the navigation command until the dispatcher has finished the layout pass that puts the skeleton on screen (the final present to the screen adds at most one frame). The UI-thread gap is the longest an input-priority message posted from another thread waited for the UI thread while a screen loaded over a 250 ms-per-request link, first visits included. The API-call count includes retries and lazy requests that land within 400 ms of the load.
- **Cold dashboard**: a fresh process each time (`PerfPhase=cold`): warm-up as at app start, sign-in, then the dashboard is loaded; the clock starts when the response body is fully in memory and stops when a rendered bitmap of the page shows both chart series (each look renders the page, which is part of the time).
- Nothing is shown on the desktop and no focus is taken: every view is hosted in a window whose parent is a message-only window, and the launch window is off-screen and not activated.


## Consecutive runs

`run-perf.ps1 -AssertBudgets` was run twice in a row on the quiet machine, both exiting 0 with every budget met (the table above is the second run). The first run (09:01) differed only in noise; its tightest figure was the UI thread gap at 49.9 ms worst (second run: 39.7 ms), so that budget has the least headroom. Planted failures that prove each check can fail (a 200 ms delay in one endpoint, among others) are recorded in the task record, not in this file.
