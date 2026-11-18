# Data retention and deletion

This rule covers every donor and patient record Lifestream holds. It is the written rule required by INFRA-20.

## 1. The public demo holds synthetic data only

- The demo database is filled by a deterministic generator in `Lifestream.Data` (names are generated, phone numbers are
  in the reserved fictional range `+1-555-01xx`, addresses are invented). No row is copied from a real person, from the
  old 1.x database or from any backup.
- A test scans the generated data for anything outside those patterns and fails the build when it finds a hit.
- The demo is reset to the generator's output once per UTC day (and on request, at most every 10 minutes), so anything a
  visitor types is deleted by the next reset. Visitors are told not to enter real personal information.
- The old course-project data (the 1.x `BloodBank` database and its `.bacpac` export) is not carried over and is not
  kept in this repository.

## 2. Real deployments (if anyone runs Lifestream with real people)

Lifestream is a portfolio and teaching project, not a certified medical system. Anyone who deploys it for real records is
the data controller and must check the rules of their own jurisdiction (for example GDPR in the EU and UK, HIPAA in the
United States). Recommended default retention rule, which an operator may tighten:

| Record | Kept for | Then |
|---|---|---|
| Donor and patient personal details | While the person is an active donor or patient, plus 24 months after the last activity | Deleted or anonymised |
| Donation and issue receipts, blood bag history | 10 years (traceability of blood products) | Personal details replaced by a code; the unit history stays |
| Audit log | 12 months | Deleted |
| Refresh tokens | Until they expire (at most 30 days) | Deleted |

## 3. Deletion on request

- A person can ask for their record to be deleted. The operator deletes or anonymises it within 30 days and confirms in
  writing.
- Deleting a donor or patient removes their personal fields. Receipts that must be kept for traceability keep only the
  blood type, quantity and dates.
- Backups age out on the database's own retention schedule (14 days of point-in-time restore).

## 4. Review

The rule is reviewed once a year. The review is recorded by changing the date below.

Last reviewed: 2026-11-19.
