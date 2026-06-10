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
        ", ct);
    }

    public async Task<XmlParsingRunResult> ParseDownloadedXmlAsync(
        int? facilityId = null,
        bool rebuild = false,
        Func<XmlParsingRunProgress, Task>? onProgress = null,
        CancellationToken ct = default)
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

        var txQuery = _db.PortalTransactions
            .AsNoTracking()
            .Where(t => (t.Portal == "DHA"
                            && t.FileDownloaded
                            && t.FileContentXml != null
                            && t.FileContentXml.Length > 10)
                     // RHA/Riyati delivers JSON inline (no file download) — parse from RawXml
                     || (t.Portal == "RHA"
                            && t.RawXml != null
                            && t.RawXml.Length > 2));

        if (facilityId.HasValue)
            txQuery = txQuery.Where(t => t.FacilityId == facilityId.Value);

        if (!rebuild)
        {
            // Pre-load already-parsed transaction IDs into a HashSet to avoid a correlated subquery per row
            var alreadyParsedQuery = _db.XmlParsedRecords.AsNoTracking()
                .Select(r => r.PortalTransactionId)
                .Distinct();
            if (facilityId.HasValue)
                alreadyParsedQuery = _db.XmlParsedRecords.AsNoTracking()
                    .Where(r => r.FacilityId == facilityId.Value)
                    .Select(r => r.PortalTransactionId)
                    .Distinct();
            var alreadyParsedIds = (await alreadyParsedQuery.ToListAsync(ct)).ToHashSet();
            txQuery = txQuery.Where(t => !alreadyParsedIds.Contains(t.Id));
        }

        var total = await txQuery.CountAsync(ct);
        var result = new XmlParsingRunResult { FilesScanned = total };

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

        // A claim ref is "matched" only when a Submission AND a Remittance exist
        // for the SAME (FacilityId, ClaimId). Correlate on both columns via EXISTS
        // so a ClaimId reused across facilities can never cross-match, and compare
        // ClaimId case-insensitively (COLLATE NOCASE) to align with the C# counts.
        if (facilityId.HasValue)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""XmlParsedRecords""
                SET ""IsMatched"" = 0, ""MatchedAt"" = NULL
                WHERE ""FacilityId"" = {facilityId.Value};

                UPDATE ""XmlParsedRecords""
                SET ""IsMatched"" = 1, ""MatchedAt"" = datetime('now')
                WHERE ""FacilityId"" = {facilityId.Value}
                  AND EXISTS (
                    SELECT 1
                    FROM ""XmlParsedRecords"" m
                    WHERE m.""FacilityId"" = ""XmlParsedRecords"".""FacilityId""
                      AND m.""ClaimId"" = ""XmlParsedRecords"".""ClaimId"" COLLATE NOCASE
                    GROUP BY m.""FacilityId"", m.""ClaimId"" COLLATE NOCASE
                    HAVING SUM(CASE WHEN m.""RecordKind"" = 'Submission' THEN 1 ELSE 0 END) > 0
                       AND SUM(CASE WHEN m.""RecordKind"" = 'Remittance' THEN 1 ELSE 0 END) > 0
                  );
            ", ct);
        }
        else
        {
            await _db.Database.ExecuteSqlRawAsync(@"
                UPDATE ""XmlParsedRecords""
                SET ""IsMatched"" = 0, ""MatchedAt"" = NULL;

                UPDATE ""XmlParsedRecords""
                SET ""IsMatched"" = 1, ""MatchedAt"" = datetime('now')
                WHERE EXISTS (
                    SELECT 1
                    FROM ""XmlParsedRecords"" m
                    WHERE m.""FacilityId"" = ""XmlParsedRecords"".""FacilityId""
                      AND m.""ClaimId"" = ""XmlParsedRecords"".""ClaimId"" COLLATE NOCASE
                    GROUP BY m.""FacilityId"", m.""ClaimId"" COLLATE NOCASE
                    HAVING SUM(CASE WHEN m.""RecordKind"" = 'Submission' THEN 1 ELSE 0 END) > 0
                       AND SUM(CASE WHEN m.""RecordKind"" = 'Remittance' THEN 1 ELSE 0 END) > 0
                );
            ", ct);
        }

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
            var principalDiag = claim.Elements()
                .Where(e => e.Name.LocalName == "Diagnosis")
                .FirstOrDefault(d => ChildValue(d, "Type") == "Principal")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? "";
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

            decimal.TryParse(ChildValue(claim, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var net);

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
                NetAmount = net,
                ActivityCount = claim.Descendants().Count(e => e.Name.LocalName == "Activity"),
                IdPayer = ChildValue(claim, "IDPayer") ?? "",
                ResubmissionType = resubmissionType,
                PrincipalDiagnosis = principalDiag,
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
            var activityCount = 0;

            foreach (var activity in claim.Descendants().Where(e => e.Name.LocalName == "Activity"))
            {
                activityCount++;
                if (decimal.TryParse(ChildValue(activity, "Net"), NumberStyles.Any, CultureInfo.InvariantCulture, out var net))
                    received += net;
                if (decimal.TryParse(ChildValue(activity, "PaymentAmount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var payment))
                    paid += payment;

                var activityDenial = ChildValue(activity, "DenialCode");
                if (!string.IsNullOrWhiteSpace(activityDenial) && !denialCodes.Contains(activityDenial, StringComparer.OrdinalIgnoreCase))
                    denialCodes.Add(activityDenial);
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
                ActivityCount = activityCount,
                PaymentReference = ChildValue(claim, "PaymentReference") ?? headerPayRef,
                SettlementDate = ChildValue(claim, "DateSettlement") ?? raDate,
                DenialCodesJson = denialCodes.Count == 0 ? null : JsonSerializer.Serialize(denialCodes),
                Comments = string.Join(" | ", denialDescriptions.Distinct(StringComparer.OrdinalIgnoreCase)),
                IdPayer = ChildValue(claim, "IDPayer") ?? "",
                ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            };
        }
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
