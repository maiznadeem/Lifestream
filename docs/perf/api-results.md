# API performance results (100x and 1x demo datasets)

Produced by `PerformanceSmokeTests` (100x) and `PerformanceAtDemoSizeTests` (1x demo size) in `tests/Lifestream.Api.Tests`
(`dotnet test --filter Category=Perf`; set `LIFESTREAM_PERF_RESULTS=<file>` to write the tables). The API runs in memory
(WebApplicationFactory) over a real local SQL Server 2025 default instance. Datasets from `ScaleDataGenerator`:
- **100x**: 27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors, 3,500 patients and 1,100 staff.
- **1x demo size** (what the public demo holds after each reset): 270 receipts, 300 appointments, 150 requests, 40 donors, 35 patients, 11 staff.

Every request is made 8 times to warm up, then 40 times and timed (request sent to body read), as a signed-in Admin. p95 is the
38th of 40 sorted samples. Budget (design 5.5): p95 below 100 ms. CI reports these numbers and does not gate on them (shared
runners are too noisy); locally the tests assert the budget for every list shape below and for the default 12-month dashboard (the
36-month dashboard is measured for information, not asserted). Both runs use the same requests, including the common filter
values (status `Fulfilled`/`Completed`, blood type `OPos`), one-letter searches and deep pages.

Every list answers with one SQL command (items and total count together); name searches are prefix matches on indexed columns.
The execution plans of the same queries are checked separately by `ApiPlanMatrixTests` (see `docs/database.md`).

### List endpoints at 1x demo size (warm, local SQL Server)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET donors (page 1) | 1.6 | 3.1 | 19.2 | 7602 | p95 < 100 ms met |
| GET donors search=a | 5.3 | 15.4 | 27.8 | 980 | p95 < 100 ms met |
| GET donors bloodType=OPos sort=-lastDonated | 1.3 | 2.0 | 7.2 | 3258 | p95 < 100 ms met |
| GET donors page 60 (deep) | 1.6 | 9.2 | 14.8 | 47 | p95 < 100 ms met |
| GET patients (page 1) | 2.0 | 2.9 | 15.1 | 4939 | p95 < 100 ms met |
| GET patients search=a | 10.1 | 17.0 | 25.0 | 880 | p95 < 100 ms met |
| GET patients page 60 (deep) | 1.7 | 2.4 | 6.9 | 47 | p95 < 100 ms met |
| GET appointments (page 1) | 1.6 | 2.0 | 7.5 | 8572 | p95 < 100 ms met |
| GET appointments status=Pending | 1.6 | 1.8 | 2.0 | 8571 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos | 1.6 | 2.1 | 7.6 | 8674 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos sort=-placed page 100 | 1.6 | 2.2 | 9.6 | 48 | p95 < 100 ms met |
| GET appointments search=a, date range | 12.2 | 22.8 | 41.5 | 1235 | p95 < 100 ms met |
| GET appointments search=a sort=slot page 20 | 11.9 | 20.1 | 29.6 | 47 | p95 < 100 ms met |
| GET appointments search=Ay sort=slot page 3 | 12.2 | 15.2 | 26.8 | 45 | p95 < 100 ms met |
| GET requests (page 1) | 1.9 | 2.6 | 2.7 | 8444 | p95 < 100 ms met |
| GET requests status=Pending | 1.9 | 3.5 | 15.5 | 6749 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=requiredBy | 1.8 | 2.2 | 5.8 | 5480 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=-requiredBy page 40 | 2.1 | 2.6 | 12.6 | 47 | p95 < 100 ms met |
| GET requests search=a | 11.8 | 12.9 | 13.1 | 3936 | p95 < 100 ms met |
| GET requests search=a page 30 | 11.9 | 18.6 | 21.8 | 47 | p95 < 100 ms met |
| GET receipts (page 1) | 2.2 | 2.7 | 7.0 | 9222 | p95 < 100 ms met |
| GET receipts kind=Donation, date range | 2.1 | 2.6 | 11.6 | 8783 | p95 < 100 ms met |
| GET receipts kind=Donation bloodType=OPos | 2.4 | 3.2 | 6.4 | 9351 | p95 < 100 ms met |
| GET receipts search=a | 31.9 | 35.8 | 65.6 | 9158 | p95 < 100 ms met |
| GET receipts search=Ay | 32.2 | 38.7 | 53.9 | 402 | p95 < 100 ms met |
| GET receipts search=a page 100 | 32.7 | 39.2 | 41.2 | 48 | p95 < 100 ms met |
| GET receipts page 100 | 2.5 | 3.0 | 22.4 | 49 | p95 < 100 ms met |
| GET receipts page 500 (deep) | 2.6 | 3.7 | 7.5 | 49 | p95 < 100 ms met |
| GET appointments page 500 (deep) | 2.4 | 13.8 | 52.1 | 49 | p95 < 100 ms met |
| GET requests page 250 (deep) | 2.2 | 3.4 | 7.9 | 49 | p95 < 100 ms met |
| GET appointments donor blood type | 1.9 | 3.0 | 9.0 | 8619 | p95 < 100 ms met |
| GET staff pageSize=200 | 1.2 | 1.5 | 12.4 | 2171 | p95 < 100 ms met |
| GET stock | 1.3 | 2.1 | 6.7 | 722 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### Dashboard at 1x demo size (warm, no cache)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET dashboard (default 12 months, uncached) | 4.8 | 6.4 | 9.7 | 1629 | p95 < 100 ms met |
| GET dashboard (36 months, uncached) | 5.4 | 7.3 | 19.9 | 2709 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### List endpoints at 100x (warm, local SQL Server)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET donors (page 1) | 3.6 | 5.1 | 16.4 | 9692 | p95 < 100 ms met |
| GET donors search=a | 7.0 | 10.0 | 10.8 | 9691 | p95 < 100 ms met |
| GET donors bloodType=OPos sort=-lastDonated | 2.2 | 3.1 | 19.4 | 9609 | p95 < 100 ms met |
| GET donors page 60 (deep) | 8.4 | 9.9 | 11.6 | 9546 | p95 < 100 ms met |
| GET patients (page 1) | 15.3 | 17.4 | 17.8 | 7203 | p95 < 100 ms met |
| GET patients search=a | 21.3 | 24.1 | 25.5 | 7202 | p95 < 100 ms met |
| GET patients page 60 (deep) | 12.8 | 16.3 | 20.6 | 7151 | p95 < 100 ms met |
| GET appointments (page 1) | 6.1 | 8.8 | 9.3 | 8820 | p95 < 100 ms met |
| GET appointments status=Pending | 3.1 | 3.7 | 5.6 | 8819 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos | 4.7 | 6.8 | 8.0 | 8892 | p95 < 100 ms met |
| GET appointments status=Completed bloodType=OPos sort=-placed page 100 | 4.9 | 6.7 | 7.0 | 8926 | p95 < 100 ms met |
| GET appointments search=a, date range | 19.1 | 21.6 | 25.0 | 7967 | p95 < 100 ms met |
| GET appointments search=a sort=slot page 20 | 26.1 | 29.5 | 30.3 | 8877 | p95 < 100 ms met |
| GET appointments search=Ay sort=slot page 3 | 11.0 | 11.7 | 12.0 | 8864 | p95 < 100 ms met |
| GET requests (page 1) | 5.8 | 7.5 | 9.1 | 8647 | p95 < 100 ms met |
| GET requests status=Pending | 4.7 | 5.6 | 5.8 | 8646 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=requiredBy | 4.6 | 6.1 | 6.2 | 8617 | p95 < 100 ms met |
| GET requests status=Fulfilled bloodType=OPos sort=-requiredBy page 40 | 2.8 | 3.2 | 3.6 | 8694 | p95 < 100 ms met |
| GET requests search=a | 20.9 | 22.9 | 23.8 | 8662 | p95 < 100 ms met |
| GET requests search=a page 30 | 19.7 | 22.3 | 23.8 | 8717 | p95 < 100 ms met |
| GET receipts (page 1) | 5.4 | 6.2 | 7.8 | 9524 | p95 < 100 ms met |
| GET receipts kind=Donation, date range | 2.1 | 2.7 | 3.5 | 9686 | p95 < 100 ms met |
| GET receipts kind=Donation bloodType=OPos | 4.8 | 6.8 | 7.2 | 9674 | p95 < 100 ms met |
| GET receipts search=a | 74.2 | 86.6 | 88.4 | 9534 | p95 < 100 ms met |
| GET receipts search=Ay | 37.0 | 40.0 | 42.6 | 9510 | p95 < 100 ms met |
| GET receipts search=a page 100 | 83.4 | 94.4 | 95.7 | 9527 | p95 < 100 ms met |
| GET receipts page 100 | 9.2 | 10.9 | 12.6 | 9552 | p95 < 100 ms met |
| GET receipts page 500 (deep) | 12.3 | 13.3 | 13.7 | 9503 | p95 < 100 ms met |
| GET appointments page 500 (deep) | 9.6 | 10.8 | 11.3 | 8879 | p95 < 100 ms met |
| GET requests page 250 (deep) | 9.2 | 14.6 | 14.6 | 8744 | p95 < 100 ms met |
| GET appointments donor blood type | 7.0 | 8.1 | 9.1 | 8817 | p95 < 100 ms met |
| GET staff pageSize=200 | 2.7 | 4.7 | 10.8 | 36544 | p95 < 100 ms met |
| GET stock | 1.2 | 2.4 | 4.7 | 732 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

### Dashboard at 100x (warm, no cache)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET dashboard (default 12 months, uncached) | 7.8 | 8.2 | 8.7 | 1511 | p95 < 100 ms met |
| GET dashboard (36 months, uncached) | 15.4 | 16.9 | 17.1 | 2899 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

Notes
- The query shapes behind these numbers are in `docs/database.md` (covering indexes, one-row cross-join total, id-set search,
  `OPTION (RECOMPILE)` for searches). No threshold, page size or data was changed to reach them.
- A search term of one letter ("a") matches most names, which is the worst case for a prefix search.
- Single-machine numbers (dev PC, Windows, SQL Server on the same host): they show the API and query shapes are inside the
  budget with headroom, not what Azure SQL serverless will do after a cold start.
