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

Every list answers with one SQL command (items and total count together). The execution plans of the same queries are checked
separately by `ApiPlanMatrixTests` (see `docs/database.md`).

**The tables below predate the search of `SearchQueries`** (a word anywhere in a name, a part of a phone number; see
`docs/database.md`, "Search"): when they were measured a search was a prefix of the full name and "a" matched 13 % of the names,
where it now matches 91 %. They have not been measured again on a quiet machine; the search rows of the test now also time a last
name, a part of a name, a part of a phone number, two words, a blood group and a text nobody has. Measured side by side on a busy
machine (old and new code turn about, 100x), see "Search before and after" at the end.

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
  and, when the tables were measured, `OPTION (RECOMPILE)` for the donor, patient, appointment and request searches). No threshold, page size or data was changed to reach them.
- A search term of one letter ("a") matches most names, which is the worst case for a search.
- Single-machine numbers (dev PC, Windows, SQL Server on the same host): they show the API and query shapes are inside the
  budget with headroom, not what Azure SQL serverless will do after a cold start.

### Search before and after (100x, a busy machine, 2026-11-20)

The same test (Perf_ListEndpoints_P95Under100ms_At100x) run on 2.1.2 and on the search of SearchQueries, turn and turn about on one
machine that other work was using (a virtual machine, other test runs on the same SQL Server), at below-normal priority on 8 processors;
the quieter of two rounds each. Not comparable with the tables above, which were measured on a quiet machine; comparable with each other.
On 2.1.2 "a" and "Ay" were prefixes of the full name; now they match where any word of a name starts, and the rows without a 2.1.2 figure
found nothing or could not be asked.

| Request | 2.1.2 p50 ms | 2.1.2 p95 ms | now p50 ms | now p95 ms |
|---|---|---|---|---|
| GET donors search=a | 10 | 10 | 10 | 12 |
| GET patients search=a | 21 | 23 | 16 | 18 |
| GET appointments search=a, date range | 21 | 23 | 4 | 4 |
| GET appointments search=a sort=slot page 20 | 27 | 30 | 22 | 28 |
| GET appointments search=Ay sort=slot page 3 | 14 | 14 | 16 | 19 |
| GET requests search=a | 21 | 24 | 21 | 31 |
| GET requests search=a page 30 | 20 | 24 | 20 | 25 |
| GET receipts search=a | 47 | 53 | 71 | 79 |
| GET receipts search=Ay | 34 | 37 | 54 | 62 |
| GET receipts search=a page 100 | 46 | 49 | 68 | 75 |
| GET donors search=eastwood (last name) |  |  | 10 | 11 |
| GET donors search=ali (inside a name) |  |  | 6 | 7 |
| GET donors search=0156 (part of a phone) |  |  | 16 | 16 |
| GET donors search=eastwood selim (two words) |  |  | 5 | 6 |
| GET donors search=O- (blood group) |  |  | 1 | 1 |
| GET donors search=zzzz (nobody) |  |  | 5 | 6 |
| GET patients search=eastwood (last name) |  |  | 15 | 16 |
| GET patients search=0156 (part of a phone) |  |  | 18 | 22 |
| GET appointments search=eastwood (last name) |  |  | 10 | 12 |
| GET appointments search=ali sort=slot (inside a name) |  |  | 13 | 14 |
| GET appointments search=0156 (part of a phone) |  |  | 15 | 16 |
| GET appointments search=eastwood selim (two words) |  |  | 6 | 7 |
| GET appointments search=555 (every phone) |  |  | 50 | 56 |
| GET appointments search=zzzz (nobody) |  |  | 6 | 7 |
| GET requests search=eastwood (last name) |  |  | 9 | 10 |
| GET requests search=0156 (part of a phone) |  |  | 14 | 14 |
| GET requests search=555 (every phone) |  |  | 37 | 41 |
| GET requests search=zzzz (nobody) |  |  | 5 | 6 |
| GET receipts search=eastwood (last name) |  |  | 49 | 52 |
| GET receipts search=ali (inside a name) |  |  | 52 | 55 |
| GET receipts search=0156 (part of a phone) |  |  | 96 | 108 |
| GET receipts search=eastwood selim (two words) |  |  | 95 | 108 |
| GET receipts search=555 (every phone) |  |  | 106 | 115 |
| GET receipts search=zzzz (nobody) |  |  | 15 | 17 |
| GET receipts search=eastwood kind=Donation |  |  | 51 | 56 |
| GET staff search=eastwood (last name) |  |  | 5 | 6 |
| GET lookups/donors search=eastwood (palette) |  |  | 2 | 2 |
| GET lookups/requests search=eastwood (palette) |  |  | 5 | 6 |

- Donors, patients, appointments and requests cost what they cost or less. A name on the receipt list costs what a prefix cost.
- The receipt list is the slowest search, as it was: one or two letters now find more (71 ms against 47), and a part of a phone
  number of three to six digits, two words and "555" (every phone of the dataset) take about 100 ms and missed the budget here by 8 to 15 ms
  (p95 108, 108, 115). At the 1x demo size every search takes 1 to 3 ms, and a part of a phone number on the receipt list 10 to 12 ms.
- Every search request of the plan matrix reads at most 28,000 pages (5,100 on the receipt list); on 2.1.2 a number typed into the
  appointment search read 60,204 and "a" on the receipt list 28,964.
