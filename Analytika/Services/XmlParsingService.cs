using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

public class XmlParsingService
{
    private const string SubmissionKind = "Submission";
    private const string RemittanceKind = "Remittance";

    private readonly AppDbContext _db;
    private readonly ILogger<XmlParsingService> _logger;

    public XmlParsingService(AppDbContext db, ILogger<XmlParsingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (!_db.Database.IsSqlite()) return;

        await _db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""XmlParsedRecords"" (
                ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""PortalTransactionId"" INTEGER NOT NULL REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
                ""FacilityId""          INTEGER NOT NULL REFERENCES ""Facilities""(""Id"") ON DELETE CASCADE,
                ""RecordKind""          TEXT NOT NULL,
                ""ClaimId""             TEXT NOT NULL,
                ""FileName""            TEXT NULL,
                ""FileId""              TEXT NULL,
                ""TransactionDate""      TEXT NULL,
                ""SenderId""            TEXT NULL,
                ""ReceiverId""          TEXT NULL,
                ""ReceiverName""        TEXT NULL,
                ""PayerId""             TEXT NULL,
                ""PayerName""           TEXT NULL,
                ""PatientId""           TEXT NULL,
                ""MemberId""            TEXT NULL,
                ""TreatmentDate""       TEXT NULL,
                ""TreatmentDateEnd""    TEXT NULL,
                ""DateOfAdmission""     TEXT NULL,
                ""SubmissionDate""      TEXT NULL,
                ""EncounterType""       TEXT NULL,
                ""Clinician""           TEXT NULL,
                ""ServiceYear""         TEXT NULL,
                ""ServiceMonth""        TEXT NULL,
                ""GrossAmount""         REAL NOT NULL DEFAULT 0,
                ""NetAmount""           REAL NOT NULL DEFAULT 0,
                ""PaidAmount""          REAL NOT NULL DEFAULT 0,
                ""ActivityCount""       INTEGER NOT NULL DEFAULT 0,
                ""PaymentReference""    TEXT NULL,
                ""SettlementDate""      TEXT NULL,
                ""DenialCodesJson""     TEXT NULL,
                ""Comments""            TEXT NULL,
                ""IdPayer""             TEXT NULL,
                ""ResubmissionType""    TEXT NULL,
                ""PrincipalDiagnosis""  TEXT NULL,
                ""DiagnosesJson""       TEXT NULL,
                ""PatientGender""       TEXT NULL,
                ""PatientDob""          TEXT NULL,
                ""PatientNationalId""   TEXT NULL,
                ""ClaimCategory""       TEXT NULL,
                ""IsMatched""           INTEGER NOT NULL DEFAULT 0,
                ""ReadyForReport""      INTEGER NOT NULL DEFAULT 1,
                ""Notes""               TEXT NULL,
                ""ParsedAt""            TEXT NOT NULL,
                ""MatchedAt""           TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_PortalTransactionId""
                ON ""XmlParsedRecords""(""PortalTransactionId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Facility_Kind""
                ON ""XmlParsedRecords""(""FacilityId"", ""RecordKind"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ClaimId""
                ON ""XmlParsedRecords""(""ClaimId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ReadyForReport""
                ON ""XmlParsedRecords""(""ReadyForReport"");

            CREATE TABLE IF NOT EXISTS ""XmlParsedActivities"" (
                ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""XmlParsedRecordId""   INTEGER NOT NULL REFERENCES ""XmlParsedRecords""(""Id"") ON DELETE CASCADE,
                ""ActivityCode""        TEXT NULL,
                ""ActivityType""        TEXT NULL,
                ""Quantity""            REAL NOT NULL DEFAULT 0,
                ""Net""                 REAL NOT NULL DEFAULT 0,
                ""Gross""               REAL NOT NULL DEFAULT 0,
                ""PaymentAmount""       REAL NOT NULL DEFAULT 0,
                ""DenialCode""          TEXT NULL,
                ""Clinician""           TEXT NULL,
                ""Start""               TEXT NULL,
                ""OrderingClinician""   TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedActivities_RecordId""
                ON ""XmlParsedActivities""(""XmlParsedRecordId"");
        ", ct);

        // Add new columns to existing SQLite tables only when missing
        // (avoids EF Error-level logs from duplicate ALTER TABLE attempts).
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = _db.Database.GetDbConnection().CreateCommand())
        {
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            cmd.CommandText = @"PRAGMA table_info(""XmlParsedRecords"")";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existingColumns.Add(reader.GetString(1));
        }

        var newColumns = new (string Name, string Sql)[]
        {
            ("GrossAmount", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""GrossAmount"" REAL NOT NULL DEFAULT 0"),
            ("DiagnosesJson", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""DiagnosesJson"" TEXT NULL"),
            ("PatientGender", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientGender"" TEXT NULL"),
            ("PatientDob", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientDob"" TEXT NULL"),
            ("PatientNationalId", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""PatientNationalId"" TEXT NULL"),
            ("ClaimCategory", @"ALTER TABLE ""XmlParsedRecords"" ADD COLUMN ""ClaimCategory"" TEXT NULL"),
        };
        foreach (var (name, sql) in newColumns)
        {
            if (existingColumns.Contains(name))
                continue;
            try { await _db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Skipping XmlParsedRecords column add for {Column}", name); }
        }
    }

    public async Task<XmlParsingRunResult> ParseDownloadedXmlAsync(
        int? facilityId = null,
        bool rebuild = false,
        Func<XmlParsingRunProgress, Task>? onProgress = null,
        CancellationToken ct = default,
        // When false, skip the pre-count. On a large DB the count is a full-table
        // scan that must finish before any row streams; skipping it lets parsing
        // begin immediately (total is reported as 0/unknown). Used for big backfills.
        bool preCount = true)
    {
        await EnsureSchemaAsync(ct);

        if (rebuild)
        {
            if (facilityId.HasValue)
                await _db.XmlParsedRecords.Where(r => r.FacilityId == facilityId.Value).ExecuteDeleteAsync(ct);
            else
                await _db.XmlParsedRecords.ExecuteDeleteAsync(ct);
        }

        var payerLookup = await LoadPayerLookupAsync(ct);

        // Base filter WITHOUT the per-blob length guard. `IS NOT NULL` is answered from
        // the row header alone, whereas `length(text)` must read the whole XML blob —
        // so counting with the length guard forces a multi-GB read of the entire table
        // just to produce a progress denominator (and the parse loop then reads the same
        // blobs again). The exact length guard is applied to the streaming query below.
        var baseQuery = _db.PortalTransactions
            .AsNoTracking()
            .Where(t => (t.Portal == "DHA"
                            && t.FileDownloaded
                            && t.FileContentXml != null
                            // FOCUS: the parser only extracts Submission/Remittance records,
                            // so Prior Request/Authorization files always yield 0 rows —
                            // scanning their blobs every run was pure waste (the skip swamp).
                            && (t.Type == "Claim" || t.Type == "Remittance"))
                     // RHA/Riyati delivers JSON inline (no file download) — parse from RawXml
                     || (t.Portal == "RHA"
                            && t.RawXml != null));

        if (facilityId.HasValue)
            baseQuery = baseQuery.Where(t => t.FacilityId == facilityId.Value);

        if (!rebuild)
        {
            // Exclude already-parsed transactions with a correlated NOT EXISTS. This is
            // a per-row index seek against IX_XmlParsedRecords_PortalTransactionId, so
            // rows start streaming immediately. (Two earlier approaches were worse: a
            // materialised id list blows SQLite's parameter limit — "too many SQL
            // variables" — on a large backfill, and a NOT IN (SELECT …) forces SQLite to
            // build an ephemeral set of all ~1M parsed ids before the first row streams.)
            // A parsed record's PortalTransactionId maps to exactly one transaction, so
            // no facility scoping is needed here even for a single-facility run.
            baseQuery = baseQuery.Where(t =>
                !_db.XmlParsedRecords.Any(r => r.PortalTransactionId == t.Id));
        }

        // Progress denominator only — cheap, header-only count. A few tiny/placeholder
        // files counted here are skipped during parsing, which is fine for a total.
        // Skipped entirely (total unknown) when preCount is false so a big backfill can
        // start streaming without first scanning the whole table.
        var total = preCount ? await baseQuery.CountAsync(ct) : 0;
        var result = new XmlParsingRunResult { FilesScanned = total };

        // Stream straight from baseQuery with NO length predicate in the WHERE. Putting
        // FileContentXml.Length in the filter forces SQLite to read every candidate blob
        // — including already-parsed rows that the NOT EXISTS clause then discards — just
        // to evaluate the guard, which is what makes an all-facilities backfill crawl.
        // Without it, blobs are materialised only in the projection below, i.e. only for
        // the pending rows that survive the header-only (IS NOT NULL) + index-seek
        // (NOT EXISTS) filter. Empty/placeholder files still stream, but ParseTransaction
        // yields nothing for them and they are tallied as skipped.
        var txQuery = baseQuery;

        if (onProgress != null)
            await onProgress(new XmlParsingRunProgress("start", "Preparing downloaded XML records", 0, total, result));

        var processed = 0;
        var pendingRows = 0;

        await foreach (var tx in txQuery
            .Select(t => new PortalTransaction
            {
                Id = t.Id,
                Portal = t.Portal,
                FacilityId = t.FacilityId,
                TransactionId = t.TransactionId,
                Type = t.Type,
                Status = t.Status,
                Direction = t.Direction,
                FileId = t.FileId,
                FileName = t.FileName,
                FileContentXml = t.FileContentXml,
                RawXml = t.RawXml,
                TransactionDate = t.TransactionDate,
                Payer = t.Payer,
                Amount = t.Amount
            })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            processed++;

            try
            {
                var records = ParseTransaction(tx, payerLookup).ToList();
                if (records.Count == 0)
                {
                    result.FilesSkipped++;
                }
                else
                {
                    _db.XmlParsedRecords.AddRange(records);
                    pendingRows += records.Count;
                    result.FilesParsed++;
                    result.RecordsSaved += records.Count;
                    result.SubmissionRows += records.Count(r => r.RecordKind == SubmissionKind);
                    result.RemittanceRows += records.Count(r => r.RecordKind == RemittanceKind);
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogWarning(ex, "Could not parse XML transaction {PortalTransactionId}", tx.Id);
            }

            if (pendingRows >= 1000 || processed % 50 == 0 || processed == total)
            {
                await _db.SaveChangesAsync(ct);
                pendingRows = 0;

                if (onProgress != null)
                    await onProgress(new XmlParsingRunProgress("parsing", $"Parsed {processed:N0} of {total:N0} downloaded file(s)", processed, total, result));
            }
        }

        if (pendingRows > 0)
            await _db.SaveChangesAsync(ct);

        var match = await MatchParsedRecordsAsync(facilityId, ct);
        result.MatchedClaimRefs = match.MatchedClaimRefs;
        result.UnmatchedSubmissions = match.UnmatchedSubmissions;
        result.UnmatchedRemittances = match.UnmatchedRemittances;

        if (onProgress != null)
            await onProgress(new XmlParsingRunProgress("done", "Parsed XML cache is ready for reports", total, total, result));

        return result;
    }

    /// <summary>
    /// Parses a caller-supplied set of PortalTransaction ids in primary-key batches.
    /// Unlike <see cref="ParseDownloadedXmlAsync"/>, this never scans the (huge, inline-blob)
    /// PortalTransactions table with a streaming predicate — the caller has already worked out
    /// which ids are pending (a cheap id-only join), so here we fetch strictly those rows by
    /// PK and read each XML blob exactly once. That makes a large backfill I/O-bound only on
    /// the blobs that actually need parsing, instead of on a full-table scan. Skips ids already
    /// present in XmlParsedRecords so it is safe to re-run. Runs one match pass at the end.
    /// </summary>
    public async Task<XmlParsingRunResult> ParsePendingByIdsAsync(
        IReadOnlyList<int> transactionIds,
        Func<XmlParsingRunProgress, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var payerLookup = await LoadPayerLookupAsync(ct);

        var total = transactionIds.Count;
        var result = new XmlParsingRunResult { FilesScanned = total };
        if (onProgress != null)
            await onProgress(new XmlParsingRunProgress("start", "Parsing pending transactions by id", 0, total, result));

        const int batchSize = 300; // keep Id IN (…) well under SQLite's parameter limit
        var processed = 0;
        var zeroYieldIds = new List<int>();   // files that parsed to 0 records — flagged per batch

        for (var offset = 0; offset < transactionIds.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = transactionIds.Skip(offset).Take(batchSize).ToList();

            var rows = await _db.PortalTransactions
                .AsNoTracking()
                .Where(t => batch.Contains(t.Id))
                .Select(t => new PortalTransaction
                {
                    Id = t.Id,
                    Portal = t.Portal,
                    FacilityId = t.FacilityId,
                    TransactionId = t.TransactionId,
                    Type = t.Type,
                    Status = t.Status,
                    Direction = t.Direction,
                    FileId = t.FileId,
                    FileName = t.FileName,
                    FileContentXml = t.FileContentXml,
                    RawXml = t.RawXml,
                    TransactionDate = t.TransactionDate,
                    Payer = t.Payer,
                    Amount = t.Amount
                })
                .ToListAsync(ct);

            foreach (var tx in rows)
            {
                processed++;
                try
                {
                    var records = ParseTransaction(tx, payerLookup).ToList();
                    if (records.Count == 0)
                    {
                        result.FilesSkipped++;
                        // Nothing extractable in this file. Record that fact, otherwise the
                        // "not yet parsed" test re-selects it on every future run forever
                        // (79,518 such files were being re-read each pass).
                        zeroYieldIds.Add(tx.Id);
                    }
                    else
                    {
                        _db.XmlParsedRecords.AddRange(records);
                        result.FilesParsed++;
                        result.RecordsSaved += records.Count;
                        result.SubmissionRows += records.Count(r => r.RecordKind == SubmissionKind);
                        result.RemittanceRows += records.Count(r => r.RecordKind == RemittanceKind);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    _logger.LogWarning(ex, "Could not parse XML transaction {PortalTransactionId}", tx.Id);
                }
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();

            // Flag this batch's zero-yield files in one statement.
            if (zeroYieldIds.Count > 0)
            {
                var flagBatch = zeroYieldIds.ToList();
                zeroYieldIds.Clear();
                try
                {
                    await _db.PortalTransactions
                        .Where(t => flagBatch.Contains(t.Id))
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.ParseYieldedNothing, true), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not flag {Count} zero-yield transactions", flagBatch.Count);
                }
            }

            if (onProgress != null)
                await onProgress(new XmlParsingRunProgress("parsing", $"Parsed {processed:N0} of {total:N0} pending transaction(s)", processed, total, result));
        }

        var match = await MatchParsedRecordsAsync(null, ct);
        result.MatchedClaimRefs = match.MatchedClaimRefs;
        result.UnmatchedSubmissions = match.UnmatchedSubmissions;
        result.UnmatchedRemittances = match.UnmatchedRemittances;

        if (onProgress != null)
            await onProgress(new XmlParsingRunProgress("done", "Parsed XML cache is ready for reports", total, total, result));

        return result;
    }

    public async Task<XmlParsingRunResult> ReparseTransactionAsync(int portalTransactionId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);

        var tx = await _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.Id == portalTransactionId)
            .Select(t => new PortalTransaction
            {
                Id = t.Id,
                Portal = t.Portal,
                FacilityId = t.FacilityId,
                TransactionId = t.TransactionId,
                Type = t.Type,
                Status = t.Status,
                Direction = t.Direction,
                FileId = t.FileId,
                FileName = t.FileName,
                FileContentXml = t.FileContentXml,
                RawXml = t.RawXml,
                TransactionDate = t.TransactionDate,
                Payer = t.Payer,
                Amount = t.Amount
            })
            .FirstOrDefaultAsync(ct);

        if (tx == null)
            return new XmlParsingRunResult { Errors = 1 };

        await _db.XmlParsedRecords.Where(r => r.PortalTransactionId == portalTransactionId).ExecuteDeleteAsync(ct);

        var payerLookup = await LoadPayerLookupAsync(ct);
        var records = ParseTransaction(tx, payerLookup).ToList();
        if (records.Count > 0)
        {
            _db.XmlParsedRecords.AddRange(records);
            await _db.SaveChangesAsync(ct);
        }

        var match = await MatchParsedRecordsAsync(tx.FacilityId, ct);

        return new XmlParsingRunResult
        {
            FilesScanned = 1,
            FilesParsed = records.Count > 0 ? 1 : 0,
            FilesSkipped = records.Count == 0 ? 1 : 0,
            RecordsSaved = records.Count,
            SubmissionRows = records.Count(r => r.RecordKind == SubmissionKind),
            RemittanceRows = records.Count(r => r.RecordKind == RemittanceKind),
            MatchedClaimRefs = match.MatchedClaimRefs,
            UnmatchedSubmissions = match.UnmatchedSubmissions,
            UnmatchedRemittances = match.UnmatchedRemittances
        };
    }

    public async Task<XmlParsingMatchResult> MatchParsedRecordsAsync(int? facilityId = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);

        // Provider-aware SQL literals (SQLite stores booleans as 0/1, Postgres as TRUE/FALSE)
        var isNpgsql = _db.Database.IsNpgsql();
        var now = isNpgsql ? "NOW()" : "datetime('now')";
        var matched = isNpgsql ? "TRUE" : "1";
        var unmatched = isNpgsql ? "FALSE" : "0";
        // Two-phase match: build the matched-claim key set ONCE with a single aggregate
        // pass, then update against that (small, indexed) temp table. Two hard-won
        // constraints shape this SQL:
        //  1. The previous correlated EXISTS-with-GROUP-BY re-aggregated the claim group
        //     for every outer row — an O(n^2)-style crawl at 1M+ rows (observed: hours,
        //     projected days).
        //  2. Keys are stored UPPER()-normalized, NOT via COLLATE NOCASE: `CREATE TABLE
        //     AS SELECT expr COLLATE NOCASE` does not persist the collation on the temp
        //     column, so its index is binary-ordered and a NOCASE comparison cannot seek
        //     it — silently reintroducing the full-scan-per-row crawl (observed live).
        //     UPPER() on both sides makes every comparison plain binary and seekable.
        var facFilter = facilityId.HasValue ? @" WHERE ""FacilityId"" = {0}" : "";
        var facFilterAnd = facilityId.HasValue ? @" AND ""FacilityId"" = {0}" : "";
        var args = facilityId.HasValue ? new object[] { facilityId.Value } : Array.Empty<object>();
        var tempKeyword = isNpgsql ? "TEMPORARY" : "TEMP";

        await _db.Database.ExecuteSqlRawAsync(
            @"DROP TABLE IF EXISTS mk_match;
            CREATE " + tempKeyword + @" TABLE mk_match (fid INTEGER NOT NULL, cid TEXT NOT NULL);
            INSERT INTO mk_match
                SELECT ""FacilityId"", UPPER(""ClaimId"")
                FROM ""XmlParsedRecords""" + facFilter + @"
                GROUP BY ""FacilityId"", UPPER(""ClaimId"")
                HAVING SUM(CASE WHEN ""RecordKind"" = 'Submission' THEN 1 ELSE 0 END) > 0
                   AND SUM(CASE WHEN ""RecordKind"" = 'Remittance' THEN 1 ELSE 0 END) > 0;
            CREATE INDEX mk_match_ix ON mk_match(fid, cid);

            UPDATE ""XmlParsedRecords""
            SET ""IsMatched"" = " + unmatched + @", ""MatchedAt"" = NULL" + facFilter + @";

            UPDATE ""XmlParsedRecords""
            SET ""IsMatched"" = " + matched + @", ""MatchedAt"" = " + now + @"
            WHERE EXISTS (
                SELECT 1 FROM mk_match
                WHERE mk_match.fid = ""XmlParsedRecords"".""FacilityId""
                  AND mk_match.cid = UPPER(""XmlParsedRecords"".""ClaimId"")
            )" + facFilterAnd + @";
            DROP TABLE IF EXISTS mk_match;
            ", args, ct);

        var query = _db.XmlParsedRecords.AsNoTracking().Where(r => r.ReadyForReport);
        if (facilityId.HasValue)
            query = query.Where(r => r.FacilityId == facilityId.Value);

        var rows = await query
            .Select(r => new { r.FacilityId, r.ClaimId, r.RecordKind })
            .ToListAsync(ct);

        // Facility-scoped, case-insensitive key so counts match the SQL above
        // (no cross-facility collisions; "abc" == "ABC" like COLLATE NOCASE).
        static string Key(int facilityId, string? claimId) => facilityId + "|" + (claimId ?? "").ToUpperInvariant();

        var submissionKeys = rows.Where(r => r.RecordKind == SubmissionKind).Select(r => Key(r.FacilityId, r.ClaimId)).ToHashSet();
        var remittanceKeys = rows.Where(r => r.RecordKind == RemittanceKind).Select(r => Key(r.FacilityId, r.ClaimId)).ToHashSet();

        return new XmlParsingMatchResult
        {
            MatchedClaimRefs = submissionKeys.Count(k => remittanceKeys.Contains(k)),
            UnmatchedSubmissions = submissionKeys.Count(k => !remittanceKeys.Contains(k)),
            UnmatchedRemittances = remittanceKeys.Count(k => !submissionKeys.Contains(k))
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadPayerLookupAsync(CancellationToken ct)
    {
        var rows = await _db.DhpoCodingSets
            .AsNoTracking()
            .Where(x => x.Category == "Payer")
            .Select(x => new { x.Code, x.Name })
            .ToListAsync(ct);

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Code) && !string.IsNullOrWhiteSpace(row.Name))
                lookup[row.Code.Trim()] = row.Name.Trim();
        }

        return lookup;
    }

    private static IEnumerable<XmlParsedRecord> ParseTransaction(
        PortalTransaction tx,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        // RHA/Riyati is a distinct service: it returns JSON (stored in RawXml),
        // not DHPO XML, so it is parsed on its own path.
        if (string.Equals(tx.Portal, "RHA", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in ParseRhaJson(tx, payerLookup))
                yield return r;
            yield break;
        }

        if (string.IsNullOrWhiteSpace(tx.FileContentXml))
            yield break;

        XDocument doc;
        try { doc = XDocument.Parse(tx.FileContentXml); }
        catch { yield break; }

        var rootName = doc.Root?.Name.LocalName ?? "";
        if (string.Equals(rootName, "Claim.Submission", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var record in ParseSubmission(tx, doc, payerLookup))
                yield return record;
        }
        else if (string.Equals(rootName, "Remittance.Advice", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var record in ParseRemittance(tx, doc))
                yield return record;
        }
    }

    // ── RHA / Riyati JSON parsing (distinct service from DHA) ────────
    // Riyati's REST API returns JSON (stored in PortalTransaction.RawXml).
    // Each row is one claim/remittance. Field names use fallbacks — adjust
    // these keys if the live Riyati payload differs.
    private static IEnumerable<XmlParsedRecord> ParseRhaJson(
        PortalTransaction tx,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        if (string.IsNullOrWhiteSpace(tx.RawXml)) return Array.Empty<XmlParsedRecord>();

        var isRemit = string.Equals(tx.Type, "Remittance", StringComparison.OrdinalIgnoreCase);
        var isClaim = string.Equals(tx.Type, "Claim", StringComparison.OrdinalIgnoreCase);
        if (!isRemit && !isClaim) return Array.Empty<XmlParsedRecord>();  // skip PriorAuth etc.

        try
        {
            using var doc = JsonDocument.Parse(tx.RawXml);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<XmlParsedRecord>();

            var claimId = (JsonStr(root, "claimId", "claimID", "ClaimId", "id", "transactionId") ?? tx.FileId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(claimId)) return Array.Empty<XmlParsedRecord>();

            var payerId = JsonStr(root, "payerId", "payerID", "insurerId", "receiverId") ?? "";
            var payerName = JsonStr(root, "payerName", "insurerName", "payer", "receiverName");
            var submissionDate = JsonStr(root, "submissionDate", "date", "claimDate", "transactionDate");
            var treatmentDate = JsonStr(root, "treatmentDate", "encounterStart", "serviceDate", "start") ?? submissionDate;

            string serviceYear = "", serviceMonth = "";
            if (DateTime.TryParse(treatmentDate ?? submissionDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                serviceYear = dt.Year.ToString(CultureInfo.InvariantCulture);
                serviceMonth = dt.ToString("MMMM", CultureInfo.InvariantCulture);
            }

            var rec = new XmlParsedRecord
            {
                PortalTransactionId = tx.Id,
                FacilityId = tx.FacilityId,
                RecordKind = isRemit ? RemittanceKind : SubmissionKind,
                ClaimId = claimId,
                FileName = tx.FileName,
                FileId = tx.FileId,
                TransactionDate = tx.TransactionDate,
                PayerId = payerId,
                PayerName = !string.IsNullOrWhiteSpace(payerName) ? payerName : ResolveLookupName(payerId, payerLookup),
                MemberId = JsonStr(root, "memberId", "memberID", "membershipNo", "policyNo") ?? "",
                PatientId = JsonStr(root, "patientId", "patientID", "emiratesId", "eid") ?? "",
                Clinician = JsonStr(root, "clinician", "clinicianId", "doctor", "provider", "orderingClinician") ?? "",
                PrincipalDiagnosis = JsonStr(root, "principalDiagnosis", "primaryDiagnosis", "diagnosis", "icd", "icd10") ?? "",
                TreatmentDate = treatmentDate,
                TreatmentDateEnd = JsonStr(root, "treatmentEnd", "encounterEnd", "end"),
                SubmissionDate = submissionDate,
                ServiceYear = serviceYear,
                ServiceMonth = serviceMonth,
                NetAmount = JsonDec(root, "net", "grossAmount", "amount", "totalAmount", "claimAmount", "billedAmount"),
                ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            };

            if (isRemit)
            {
                rec.PaidAmount = JsonDec(root, "paymentAmount", "paidAmount", "paid", "settlementAmount", "netPaid");
                rec.PaymentReference = JsonStr(root, "paymentReference", "paymentRef", "transactionReference");
                rec.SettlementDate = JsonStr(root, "settlementDate", "paymentDate", "date");
                var denials = CollectRhaDenials(root);
                if (denials.Count > 0) rec.DenialCodesJson = JsonSerializer.Serialize(denials);
            }

            return new[] { rec };
        }
        catch { return Array.Empty<XmlParsedRecord>(); }
    }

    private static string? JsonStr(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.ToString();
            }
        return null;
    }

    private static decimal JsonDec(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
            }
        return 0m;
    }

    private static List<string> CollectRhaDenials(JsonElement root)
    {
        var list = new List<string>();
        var single = JsonStr(root, "denialCode", "denialCodes", "rejectionCode", "denial");
        if (!string.IsNullOrWhiteSpace(single)) list.Add(single!);
        foreach (var arrKey in new[] { "activities", "denials", "lines", "items" })
            if (root.TryGetProperty(arrKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var it in arr.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var dc = JsonStr(it, "denialCode", "denialCodes", "rejectionCode", "denial");
                    if (!string.IsNullOrWhiteSpace(dc)) list.Add(dc!);
                }
        return list.Distinct().ToList();
    }

    private static IEnumerable<XmlParsedRecord> ParseSubmission(
        PortalTransaction tx,
        XDocument doc,
        IReadOnlyDictionary<string, string> payerLookup)
    {
        var header = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        var senderId = ChildValue(header, "SenderID") ?? "";
        var receiverId = ChildValue(header, "ReceiverID") ?? "";
        var submissionDate = ChildValue(header, "TransactionDate") ?? tx.TransactionDate ?? "";

        if (receiverId.StartsWith("DHA-F-", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var claim in doc.Descendants().Where(e => e.Name.LocalName == "Claim"))
        {
            var claimId = ChildValue(claim, "ID") ?? "";
            if (string.IsNullOrWhiteSpace(claimId))
                continue;

            var enc = claim.Elements().FirstOrDefault(e => e.Name.LocalName == "Encounter");
            var treatStart = ChildValue(enc, "Start") ?? "";
            var treatEnd = ChildValue(enc, "End") ?? "";
            var encTypeRaw = ChildValue(enc, "Type") ?? "";
            var clinician = claim.Descendants().FirstOrDefault(e => e.Name.LocalName == "Activity")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Clinician")?.Value ?? "";

            // All diagnoses (principal + secondary)
            var diagnoses = claim.Elements()
                .Where(e => e.Name.LocalName == "Diagnosis")
                .Select(d => new
                {
                    Type = ChildValue(d, "Type") ?? "",
                    Code = d.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? ""
                })
                .Where(d => !string.IsNullOrWhiteSpace(d.Code))
                .ToList();

            var principalDiag = diagnoses.FirstOrDefault(d => d.Type == "Principal")?.Code ?? "";
            string? diagnosesJson = diagnoses.Count > 0
                ? JsonSerializer.Serialize(diagnoses.Select(d => new { d.Type, d.Code }))
                : null;

            // Patient demographics
            var patient = claim.Elements().FirstOrDefault(e => e.Name.LocalName == "Patient");
            var patientGender = ChildValue(patient, "Gender") ?? "";
            var patientDob = ChildValue(patient, "DateOfBirth") ?? ChildValue(patient, "DOB") ?? "";
            var patientNationalId = ChildValue(patient, "NationalID") ?? ChildValue(patient, "EmiratesID") ?? "";

            var payerId = ChildValue(claim, "PayerID") ?? "";
            var resubmission = claim.Elements().FirstOrDefault(e => e.Name.LocalName == "Resubmission")
                            ?? claim.Descendants().FirstOrDefault(e => e.Name.LocalName == "Resubmission");
            var resubmissionType = ChildValue(resubmission, "Type") ?? "";

            var serviceYear = "";
            var serviceMonth = "";
            var admissionDate = "";
            if (DateTime.TryParseExact(treatStart, "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
            {
                serviceYear = td.Year.ToString(CultureInfo.InvariantCulture);
                serviceMonth = td.ToString("MMMM", CultureInfo.InvariantCulture);
                if (encTypeRaw == "2")
                    admissionDate = treatStart;
            }

            decimal.TryParse(ChildValue(claim, "Gross"), NumberStyles.Any, CultureInfo.InvariantCulture, out var gross);
            decimal.TryParse(ChildValue(claim, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var net);

            // Activity-level detail
            var activities = new List<XmlParsedActivity>();
            foreach (var act in claim.Descendants().Where(e => e.Name.LocalName == "Activity"))
            {
                decimal.TryParse(ChildValue(act, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var actNet);
                decimal.TryParse(ChildValue(act, "Gross"), NumberStyles.Any, CultureInfo.InvariantCulture, out var actGross);
                decimal.TryParse(ChildValue(act, "Quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty);

                activities.Add(new XmlParsedActivity
                {
                    ActivityCode = ChildValue(act, "Code") ?? "",
                    ActivityType = ChildValue(act, "Type") ?? "",
                    Quantity = qty,
                    Net = actNet,
                    Gross = actGross,
                    Clinician = ChildValue(act, "Clinician") ?? "",
                    Start = ChildValue(act, "Start") ?? "",
                    OrderingClinician = ChildValue(act, "OrderingClinician") ?? ""
                });
            }

            yield return new XmlParsedRecord
            {
                PortalTransactionId = tx.Id,
                FacilityId = tx.FacilityId,
                RecordKind = SubmissionKind,
                ClaimId = claimId.Trim(),
                FileName = tx.FileName,
                FileId = tx.FileId,
                TransactionDate = tx.TransactionDate,
                SenderId = senderId,
                ReceiverId = receiverId,
                ReceiverName = ResolveLookupName(receiverId, payerLookup),
                PayerId = payerId,
                PayerName = ResolveLookupName(payerId, payerLookup),
                PatientId = ChildValue(enc, "PatientID") ?? "",
                MemberId = ChildValue(claim, "MemberID") ?? "",
                TreatmentDate = treatStart,
                TreatmentDateEnd = treatEnd,
                DateOfAdmission = admissionDate,
                SubmissionDate = submissionDate,
                EncounterType = MapEncounterType(encTypeRaw),
                Clinician = clinician,
                ServiceYear = serviceYear,
                ServiceMonth = serviceMonth,
                GrossAmount = gross,
                NetAmount = net,
                ActivityCount = activities.Count,
                IdPayer = ChildValue(claim, "IDPayer") ?? "",
                ResubmissionType = resubmissionType,
                PrincipalDiagnosis = principalDiag,
                DiagnosesJson = diagnosesJson,
                PatientGender = patientGender,
                PatientDob = patientDob,
                PatientNationalId = patientNationalId,
                Activities = activities,
                ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            };
        }
    }

    private static IEnumerable<XmlParsedRecord> ParseRemittance(PortalTransaction tx, XDocument doc)
    {
        var header = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        var senderId = ChildValue(header, "SenderID") ?? "";
        var receiverId = ChildValue(header, "ReceiverID") ?? "";
        var raDate = ChildValue(header, "TransactionDate") ?? tx.TransactionDate ?? "";
        var headerPayRef = ChildValue(header, "PaymentReference") ?? "";

        foreach (var claim in doc.Descendants().Where(e => e.Name.LocalName == "Claim"))
        {
            var claimId = ChildValue(claim, "ID") ?? ChildValue(claim, "ClaimID") ?? "";
            if (string.IsNullOrWhiteSpace(claimId))
                continue;

            decimal received = 0m;
            decimal paid = 0m;
            var denialCodes = new List<string>();
            var denialDescriptions = new List<string>();
            var activities = new List<XmlParsedActivity>();

            foreach (var activity in claim.Descendants().Where(e => e.Name.LocalName == "Activity"))
            {
                decimal.TryParse(ChildValue(activity, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var net);
                decimal.TryParse(ChildValue(activity, "PaymentAmount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var payment);
                decimal.TryParse(ChildValue(activity, "Gross"), NumberStyles.Any, CultureInfo.InvariantCulture, out var actGross);
                decimal.TryParse(ChildValue(activity, "Quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty);
                received += net;
                paid += payment;

                var activityDenial = ChildValue(activity, "DenialCode");
                if (!string.IsNullOrWhiteSpace(activityDenial) && !denialCodes.Contains(activityDenial, StringComparer.OrdinalIgnoreCase))
                    denialCodes.Add(activityDenial);

                activities.Add(new XmlParsedActivity
                {
                    ActivityCode = ChildValue(activity, "Code") ?? "",
                    ActivityType = ChildValue(activity, "Type") ?? "",
                    Quantity = qty,
                    Net = net,
                    Gross = actGross,
                    PaymentAmount = payment,
                    DenialCode = activityDenial ?? "",
                    Clinician = ChildValue(activity, "Clinician") ?? "",
                    Start = ChildValue(activity, "Start") ?? "",
                    OrderingClinician = ChildValue(activity, "OrderingClinician") ?? ""
                });
            }

            foreach (var denial in claim.Descendants().Where(e => e.Name.LocalName == "Denial"))
            {
                var denialCode = ChildValue(denial, "Code");
                if (!string.IsNullOrWhiteSpace(denialCode) && !denialCodes.Contains(denialCode, StringComparer.OrdinalIgnoreCase))
                    denialCodes.Add(denialCode);

                var denialDesc = ChildValue(denial, "Description");
                if (!string.IsNullOrWhiteSpace(denialDesc))
                    denialDescriptions.Add(denialDesc);
            }

            var claimComments = ChildValue(claim, "Comments");
            if (!string.IsNullOrWhiteSpace(claimComments))
                denialDescriptions.Add(claimComments);

            yield return new XmlParsedRecord
            {
                PortalTransactionId = tx.Id,
                FacilityId = tx.FacilityId,
                RecordKind = RemittanceKind,
                ClaimId = claimId.Trim(),
                FileName = tx.FileName,
                FileId = tx.FileId,
                TransactionDate = tx.TransactionDate,
                SenderId = senderId,
                ReceiverId = receiverId,
                NetAmount = received,
                PaidAmount = paid,
                ActivityCount = activities.Count,
                PaymentReference = ChildValue(claim, "PaymentReference") ?? headerPayRef,
                SettlementDate = ChildValue(claim, "DateSettlement") ?? raDate,
                DenialCodesJson = denialCodes.Count == 0 ? null : JsonSerializer.Serialize(denialCodes),
                Comments = string.Join(" | ", denialDescriptions.Distinct(StringComparer.OrdinalIgnoreCase)),
                IdPayer = ChildValue(claim, "IDPayer") ?? "",
                ClaimCategory = CategorizeFromDenialCodes(denialCodes),
                Activities = activities,
                ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            };
        }
    }

    private static string CategorizeFromDenialCodes(List<string> denialCodes)
    {
        if (denialCodes.Count == 0) return "None";

        var hasTechnical = false;
        var hasMedical = false;

        foreach (var code in denialCodes)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            var upper = code.Trim().ToUpperInvariant();

            // DHA denial code patterns:
            // Technical/Administrative: eligibility, duplicate, billing format, missing info
            if (upper.StartsWith("INE", StringComparison.Ordinal) ||   // Ineligible
                upper.StartsWith("DUP", StringComparison.Ordinal) ||   // Duplicate
                upper.StartsWith("ADM", StringComparison.Ordinal) ||   // Administrative
                upper.StartsWith("BIL", StringComparison.Ordinal) ||   // Billing
                upper.StartsWith("ELG", StringComparison.Ordinal) ||   // Eligibility
                upper.StartsWith("MIS", StringComparison.Ordinal) ||   // Missing info
                upper.StartsWith("FRM", StringComparison.Ordinal) ||   // Format
                upper.StartsWith("AUT", StringComparison.Ordinal) ||   // Authorization
                upper.StartsWith("PRE", StringComparison.Ordinal) ||   // Pre-authorization
                upper.StartsWith("COV", StringComparison.Ordinal) ||   // Coverage
                upper.StartsWith("REF", StringComparison.Ordinal) ||   // Referral
                upper.StartsWith("TIM", StringComparison.Ordinal))     // Timeliness
                hasTechnical = true;
            // Medical: clinical necessity, medical review, procedure-related
            else if (upper.StartsWith("MED", StringComparison.Ordinal) ||  // Medical necessity
                     upper.StartsWith("CLI", StringComparison.Ordinal) ||  // Clinical
                     upper.StartsWith("PRO", StringComparison.Ordinal) ||  // Procedure
                     upper.StartsWith("DRG", StringComparison.Ordinal) ||  // DRG
                     upper.StartsWith("BUN", StringComparison.Ordinal) ||  // Bundling
                     upper.StartsWith("INC", StringComparison.Ordinal) ||  // Inclusive
                     upper.StartsWith("FRQ", StringComparison.Ordinal) ||  // Frequency
                     upper.StartsWith("AGE", StringComparison.Ordinal) ||  // Age-related
                     upper.StartsWith("GEN", StringComparison.Ordinal))    // Gender-related
                hasMedical = true;
            else
                hasTechnical = true; // default unknown codes to Technical
        }

        if (hasTechnical && hasMedical) return "Mixed";
        if (hasMedical) return "Medical";
        return "Technical";
    }

    private static string? ChildValue(XElement? element, string localName)
    {
        if (element == null)
            return null;

        return element.Elements()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value
            ?.Trim();
    }

    private static string MapEncounterType(string? code) => code switch
    {
        "1" => "Outpatient",
        "2" => "Inpatient",
        "3" => "Emergency",
        "4" => "Dental",
        _ => code ?? ""
    };

    private static string ResolveLookupName(string code, IReadOnlyDictionary<string, string> lookup)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";

        return lookup.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
    }
}

public class XmlParsingRunResult
{
    public int FilesScanned { get; set; }
    public int FilesParsed { get; set; }
    public int FilesSkipped { get; set; }
    public int RecordsSaved { get; set; }
    public int SubmissionRows { get; set; }
    public int RemittanceRows { get; set; }
    public int MatchedClaimRefs { get; set; }
    public int UnmatchedSubmissions { get; set; }
    public int UnmatchedRemittances { get; set; }
    public int Errors { get; set; }
}

public record XmlParsingRunProgress(
    string Status,
    string Message,
    int Done,
    int Total,
    XmlParsingRunResult Result);

public class XmlParsingMatchResult
{
    public int MatchedClaimRefs { get; set; }
    public int UnmatchedSubmissions { get; set; }
    public int UnmatchedRemittances { get; set; }
}
