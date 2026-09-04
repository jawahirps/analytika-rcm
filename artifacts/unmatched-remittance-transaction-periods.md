# Unmatched Remittance Transaction Periods

Generated: 2026-09-04 08:12:36 +04:00

## Status

The live unmatched-RA transaction-period aggregation is still in progress. No month counts are included until the read-only query completes; this report does not infer or estimate missing results.

## Matching Definition

An RA is included when its parsed record satisfies all of the following:

- `RecordKind = Remittance`
- `ReadyForReport = true`
- `IsMatched = false`
- A non-empty claim ID is present

## Transaction Period

The report groups each unmatched RA by the portal transaction `SyncPeriod` (`YYYY-MM`). If that value is absent, it falls back to the RA transaction date and then the settlement date.

## Collection Notes

The source database is the live Analytika SQLite store. The query runs detached at idle priority to avoid competing with the web application. The application remained available on `http://localhost:5000/` during collection.

## Pending Results

| Transaction period | Unmatched RA records | Facilities | First transaction date | Last transaction date |
| --- | ---: | ---: | --- | --- |
| Pending aggregation | - | - | - | - |
