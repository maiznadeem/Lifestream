# API performance results (100x dataset)

Produced by `PerformanceSmokeTests` (`dotnet test --filter Category=Perf`, set `LIFESTREAM_PERF_RESULTS=<file>` to write the tables).
The API runs in memory (WebApplicationFactory) over a real local SQL Server 2025 default instance holding the 100x dataset:
27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors, 3,500 patients and 1,100 staff from
`ScaleDataGenerator.Generate(100)`. Every request is made 8 times to warm up, then 40 times and timed (request sent to body read),
as a signed-in Admin. p95 is the 38th of 40 sorted samples. Budget (design 5.5): p95 below 100 ms. CI reports these numbers and does
not gate on them (shared runners are too noisy); locally the tests assert the budget for every list shape below and for the default
12-month dashboard (the 36-month dashboard is measured for information, not asserted).

Every list answers with one SQL command (items and total count together); name searches are prefix matches on the indexed,
case- and accent-insensitive name columns.

## Dashboard at 100x (warm, no cache)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET dashboard (default 12 months, uncached) | 24.8 | 30.5 | 41.8 | 1511 | p95 < 100 ms met |
| GET dashboard (36 months, uncached) | 38.4 | 45.5 | 49.8 | 2899 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)

## List endpoints at 100x (warm, local SQL Server)

| Request | p50 ms | p95 ms | max ms | response bytes | budget |
|---|---|---|---|---|---|
| GET donors (page 1) | 1.8 | 2.2 | 3.0 | 8692 | p95 < 100 ms met |
| GET donors search=a | 3.7 | 4.6 | 5.4 | 8691 | p95 < 100 ms met |
| GET donors bloodType=OPos sort=-lastDonated | 2.8 | 3.2 | 3.8 | 8609 | p95 < 100 ms met |
| GET patients (page 1) | 7.7 | 9.6 | 11.0 | 7203 | p95 < 100 ms met |
| GET patients search=a | 6.2 | 7.0 | 7.3 | 7202 | p95 < 100 ms met |
| GET appointments (page 1) | 7.1 | 7.8 | 8.1 | 8820 | p95 < 100 ms met |
| GET appointments status=Pending | 5.0 | 5.7 | 6.8 | 8819 | p95 < 100 ms met |
| GET appointments search=a, date range | 6.4 | 7.3 | 7.5 | 7967 | p95 < 100 ms met |
| GET requests (page 1) | 4.3 | 5.8 | 6.1 | 8647 | p95 < 100 ms met |
| GET requests status=Pending | 4.8 | 5.3 | 5.5 | 8646 | p95 < 100 ms met |
| GET requests search=a | 8.7 | 9.5 | 9.7 | 8662 | p95 < 100 ms met |
| GET receipts (page 1) | 4.6 | 5.4 | 5.6 | 9524 | p95 < 100 ms met |
| GET receipts kind=Donation, date range | 2.9 | 3.8 | 4.4 | 9686 | p95 < 100 ms met |
| GET receipts search=a | 52.6 | 57.3 | 77.3 | 9534 | p95 < 100 ms met |
| GET receipts page 100 | 12.8 | 16.6 | 17.0 | 9552 | p95 < 100 ms met |
| GET receipts page 500 (deep) | 28.9 | 31.9 | 36.2 | 9503 | p95 < 100 ms met |
| GET appointments page 500 (deep) | 29.0 | 32.5 | 33.4 | 8879 | p95 < 100 ms met |
| GET requests page 250 (deep) | 17.3 | 21.9 | 22.2 | 8744 | p95 < 100 ms met |
| GET appointments donor blood type | 3.5 | 4.2 | 4.5 | 8817 | p95 < 100 ms met |
| GET staff pageSize=200 | 1.8 | 2.2 | 2.6 | 36544 | p95 < 100 ms met |
| GET stock | 0.8 | 1.0 | 1.1 | 732 | p95 < 100 ms met |

Server: Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)


Notes
- Receipt search was the slow shape while it was a join with an OR across the staff, donor and patient names (p95 104 ms and,
  with an IN-list variant, 145 ms). It now uses the data module's `PartyNameStartsWith` (name, then appointment or request, then
  receipt, joined by UNION) plus a staff-name path, as the database notes describe; p95 about 47 ms. Nothing else was changed to
  reach these numbers (no thresholds, page sizes or data were altered).
- A search term of one letter ("a") matches most names, which is the worst case for a prefix search.
- Single-machine numbers (dev PC, Windows, SQL Server on the same host): they show the API and query shapes are inside the
  budget with headroom, not what Azure SQL serverless will do after a cold start.
