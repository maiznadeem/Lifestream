# API performance results (100x and 1x demo datasets)

Produced by `PerformanceSmokeTests` (100x) and `PerformanceAtDemoSizeTests` (1x demo size) in `tests/Lifestream.Api.Tests`
(`dotnet test --filter Category=Perf`; set `LIFESTREAM_PERF_RESULTS=<file>` to write the tables). The API runs in memory
(WebApplicationFactory) over a real local SQL Server 2025 default instance. Datasets from `ScaleDataGenerator`:
- **100x**: 27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors, 3,500 patients and 1,100 staff.
- **1x demo size** (what the public demo holds after each reset): 270 receipts, 300 appointments, 150 requests, 40 donors, 35 patients, 11 staff.

Every request is made 8 times to warm up, then 40 times and timed (request sent to body read), as a signed-in Admin. p95 is the
38th of 40 sorted samples. Budget (design 5.5): p95 below 100 ms. CI does not run these tests (every test project leaves
`Category=Perf` out by default, and shared runners are too noisy to gate on); locally the tests assert the budget for every list shape below and for the default 12-month dashboard (the
36-month dashboard is measured for information, not asserted). Both runs use the same requests, including the common filter
values (status `Fulfilled`/`Completed`, blood type `OPos`), one-letter searches and deep pages.

The gate runs the whole suite at the same time, so a shape that misses the budget is measured again (up to 3 rounds in all, best round
counts; threshold unchanged): a scan or a slow query misses every round, a burst of load from elsewhere does not. The tables below
were produced on a quiet machine and every shape met the budget in its first round.

Every list answers with one SQL command (items and total count together); name searches are prefix matches on indexed columns.
The execution plans of the same queries are checked separately by `ApiPlanMatrixTests` (see `docs/database.md`).

### List endpoints at 1x demo size (warm, local SQL Server)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET donors (page 1) | 5.3 | 45.5 | 52.9 | 7602 | p95 < 100 ms met |
| GET donors search=a | 9.3 | 13.5 | 14.9 | 980 | p95 < 100 ms met |
| GET donors bloodType=OPos sort=-lastDonated | 3.5 | 6.4 | 7.4 | 3258 | p95 < 100 ms met |
| GET donors page 60 (deep) | 3.6 | 8.0 | 9.5 | 47 | p95 < 100 ms met |
| GET patients (page 1) | 4.0 | 6.9 | 8.0 | 4939 | p95 < 100 ms met |
| GET patients search=a | 15.3 | 20.3 | 21.4 | 880 | p95 < 100 ms met |
| GET patients page 60 (deep) | 7.0 | 8.8 | 10.0 | 47 | p95 < 100 ms met |
| GET appointments (page 1) | 4.6 | 6.9 | 7.4 | 8572 | p95 < 100 ms met |
| GET appointments status=Pending | 3.2 | 6.2 | 8.3 | 8571 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos | 4.6 | 6.1 | 7.3 | 8674 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos sort=-placed page 100 | 2.2 | 2.9 | 3.2 | 48 | p95 < 100 ms met |
| GET appointments search=a, date range | 13.8 | 17.2 | 18.0 | 1235 | p95 < 100 ms met |
| GET appointments search=a sort=slot page 20 | 13.6 | 15.7 | 17.7 | 47 | p95 < 100 ms met |
| GET appointments search=Ay sort=slot page 3 | 12.1 | 14.0 | 18.8 | 45 | p95 < 100 ms met |
| GET requests (page 1) | 2.3 | 2.9 | 3.1 | 8444 | p95 < 100 ms met |
| GET requests status=Pending | 2.3 | 2.7 | 3.0 | 6749 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=requiredBy | 2.1 | 3.7 | 4.2 | 5480 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=-requiredBy page 40 | 2.3 | 3.2 | 3.5 | 47 | p95 < 100 ms met |
| GET requests search=a | 12.4 | 15.4 | 18.0 | 3936 | p95 < 100 ms met |
| GET requests search=a page 30 | 13.6 | 15.7 | 16.2 | 47 | p95 < 100 ms met |
| GET receipts (page 1) | 2.6 | 3.1 | 3.3 | 9222 | p95 < 100 ms met |
| GET receipts kind=Donation, date range | 3.1 | 4.9 | 5.7 | 8783 | p95 < 100 ms met |
| GET receipts kind=Donation bloodType=OPos | 4.0 | 5.2 | 11.5 | 9351 | p95 < 100 ms met |
| GET receipts search=a | 5.6 | 8.7 | 10.0 | 9158 | p95 < 100 ms met |
| GET receipts search=Ay | 4.8 | 7.6 | 9.4 | 402 | p95 < 100 ms met |
| GET receipts search=a page 100 | 9.1 | 12.4 | 14.3 | 48 | p95 < 100 ms met |
| GET receipts page 100 | 2.5 | 3.6 | 5.8 | 49 | p95 < 100 ms met |
| GET receipts page 500 (deep) | 3.2 | 4.9 | 5.3 | 49 | p95 < 100 ms met |
| GET appointments page 500 (deep) | 2.6 | 6.3 | 9.7 | 49 | p95 < 100 ms met |
| GET requests page 250 (deep) | 3.9 | 7.2 | 8.8 | 49 | p95 < 100 ms met |
| GET appointments donor blood type | 3.2 | 4.8 | 6.0 | 8619 | p95 < 100 ms met |
| GET staff pageSize=200 | 2.3 | 2.7 | 3.9 | 2171 | p95 < 100 ms met |
| GET stock | 1.5 | 1.8 | 2.0 | 722 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### Dashboard at 1x demo size (warm, no cache)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET dashboard (default 12 months, uncached) | 5.2 | 6.8 | 8.5 | 1629 | p95 < 100 ms met |
| GET dashboard (36 months, uncached) | 6.2 | 8.0 | 8.8 | 2709 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### Dashboard at 100x (warm, no cache)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET dashboard (default 12 months, uncached) | 23.3 | 27.2 | 31.6 | 1511 | p95 < 100 ms met |
| GET dashboard (36 months, uncached) | 32.1 | 36.7 | 37.5 | 2899 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### List endpoints at 100x (warm, local SQL Server)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET donors (page 1) | 0.8 | 1.1 | 1.5 | 9692 | p95 < 100 ms met |
| GET donors search=a | 5.1 | 5.8 | 6.2 | 9691 | p95 < 100 ms met |
| GET donors bloodType=OPos sort=-lastDonated | 0.7 | 0.9 | 1.2 | 9609 | p95 < 100 ms met |
| GET donors page 60 (deep) | 1.2 | 1.5 | 2.0 | 9546 | p95 < 100 ms met |
| GET patients (page 1) | 4.9 | 6.8 | 7.7 | 7203 | p95 < 100 ms met |
| GET patients search=a | 12.7 | 19.1 | 30.2 | 7202 | p95 < 100 ms met |
| GET patients page 60 (deep) | 4.3 | 5.4 | 5.6 | 7151 | p95 < 100 ms met |
| GET appointments (page 1) | 2.1 | 2.5 | 2.6 | 8820 | p95 < 100 ms met |
| GET appointments status=Pending | 0.9 | 1.1 | 1.2 | 8819 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos | 0.9 | 1.2 | 1.7 | 8892 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos sort=-placed page 100 | 2.4 | 2.9 | 3.1 | 8926 | p95 < 100 ms met |
| GET appointments search=a, date range | 15.3 | 16.4 | 18.7 | 7967 | p95 < 100 ms met |
| GET appointments search=a sort=slot page 20 | 18.9 | 20.5 | 20.6 | 8877 | p95 < 100 ms met |
| GET appointments search=Ay sort=slot page 3 | 11.0 | 12.1 | 12.4 | 8864 | p95 < 100 ms met |
| GET requests (page 1) | 3.3 | 3.6 | 3.8 | 8647 | p95 < 100 ms met |
| GET requests status=Pending | 3.0 | 4.1 | 4.5 | 8646 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=requiredBy | 2.7 | 3.7 | 4.2 | 8617 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=-requiredBy page 40 | 1.3 | 1.8 | 2.1 | 8694 | p95 < 100 ms met |
| GET requests search=a | 16.0 | 18.1 | 18.7 | 8662 | p95 < 100 ms met |
| GET requests search=a page 30 | 16.6 | 18.9 | 19.8 | 8717 | p95 < 100 ms met |
| GET receipts (page 1) | 3.9 | 4.9 | 5.3 | 9524 | p95 < 100 ms met |
| GET receipts kind=Donation, date range | 1.9 | 2.6 | 2.8 | 9686 | p95 < 100 ms met |
| GET receipts kind=Donation bloodType=OPos | 2.1 | 2.5 | 2.9 | 9674 | p95 < 100 ms met |
| GET receipts search=a | 53.8 | 61.1 | 64.2 | 9534 | p95 < 100 ms met |
| GET receipts search=Ay | 38.6 | 42.9 | 44.7 | 9510 | p95 < 100 ms met |
| GET receipts search=a page 100 | 56.8 | 62.0 | 64.8 | 9527 | p95 < 100 ms met |
| GET receipts page 100 | 3.9 | 4.3 | 4.5 | 9552 | p95 < 100 ms met |
| GET receipts page 500 (deep) | 6.7 | 7.7 | 8.4 | 9503 | p95 < 100 ms met |
| GET appointments page 500 (deep) | 12.2 | 13.3 | 13.7 | 8879 | p95 < 100 ms met |
| GET requests page 250 (deep) | 4.7 | 5.6 | 7.4 | 8744 | p95 < 100 ms met |
| GET appointments donor blood type | 1.3 | 1.6 | 1.8 | 8817 | p95 < 100 ms met |
| GET staff pageSize=200 | 1.7 | 3.0 | 3.3 | 36544 | p95 < 100 ms met |
| GET stock | 0.7 | 0.8 | 0.9 | 732 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

Notes
- The query shapes behind these numbers are in `docs/database.md` (covering indexes, one-row cross-join total, id-set search,
  `OPTION (RECOMPILE)` for the donor, patient, appointment and request searches). No threshold, page size or data was changed to reach them.
- A search term of one letter ("a") matches most names, which is the worst case for a prefix search.
- Single-machine numbers (dev PC, Windows, SQL Server on the same host): they show the API and query shapes are inside the
  budget with headroom, not what Azure SQL serverless will do after a cold start.
