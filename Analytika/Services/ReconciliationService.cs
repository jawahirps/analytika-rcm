using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Xml.Linq;

namespace Analytika.Services;

/// <summary>
/// Reconciles Claim submission files against Remittance advice files
/// using ClaimID as the common key, per DHA Sync Strategy step 4.
/// </summary>
public class ReconciliationService
{
    private readonly AppDbContext _db;

    public ReconciliationService(AppDbContext db) => _db = db;

    // ── XML Parsing Stats Dashboard ───────────────────────────────

    public async Task<XmlParsingViewModel> GetXmlParsingStatsAsync(List<int>? facilityIds, string? search = null, string? kind = null)
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();
        kind = string.IsNullOrWhiteSpace(kind) ? "All" : kind;

        var txBase = _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.Portal == "DHA");

        if (facilityIds?.Count > 0)
            txBase = txBase.Where(t => facilityIds.Contains(t.FacilityId));

        var txCounts = await txBase
            .GroupBy(t => t.FacilityId)
            .Select(g => new
            {
                FacilityId = g.Key,
                SubmissionTotal = g.Count(t =>
                    (t.Type.Contains("Claim")
                     || (t.FileName != null && (t.FileName.StartsWith("CLM") || t.FileName.Contains("Claim"))))
                    && !(t.Type.Contains("Remittance")
                         || (t.FileName != null && (t.FileName.StartsWith("RA_") || t.FileName.Contains("Remittance"))))),
                SubmissionDownloaded = g.Count(t =>
                    t.FileDownloaded
                    && (t.Type.Contains("Claim")
                        || (t.FileName != null && (t.FileName.StartsWith("CLM") || t.FileName.Contains("Claim"))))
                    && !(t.Type.Contains("Remittance")
                         || (t.FileName != null && (t.FileName.StartsWith("RA_") || t.FileName.Contains("Remittance"))))),
                RemittanceTotal = g.Count(t =>
                    t.Type.Contains("Remittance")
                    || (t.FileName != null && (t.FileName.StartsWith("RA_") || t.FileName.Contains("Remittance")))),
                RemittanceDownloaded = g.Count(t =>
                    t.FileDownloaded
                    && (t.Type.Contains("Remittance")
                        || (t.FileName != null && (t.FileName.StartsWith("RA_") || t.FileName.Contains("Remittance")))))
            })
            .ToListAsync();

        var parsedBase = _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.ReadyForReport);
        if (facilityIds?.Count > 0)
            parsedBase = parsedBase.Where(r => facilityIds.Contains(r.FacilityId));

        var parsedRows = await parsedBase
            .Select(r => new { r.FacilityId, r.PortalTransactionId, r.RecordKind, r.ClaimId, r.IsMatched })
            .ToListAsync();

        var parsedByFacility = parsedRows
            .GroupBy(r => r.FacilityId)
            .ToDictionary(g => g.Key, g =>
            {
                var submissionIds = g.Where(r => r.RecordKind == "Submission")
                    .Select(r => r.ClaimId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remittanceIds = g.Where(r => r.RecordKind == "Remittance")
                    .Select(r => r.ClaimId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return new
                {
                    SubmissionFileCount = g.Where(r => r.RecordKind == "Submission")
                        .Select(r => r.PortalTransactionId)
                        .Distinct()
                        .Count(),
                    RemittanceFileCount = g.Where(r => r.RecordKind == "Remittance")
                        .Select(r => r.PortalTransactionId)
                        .Distinct()
                        .Count(),
                    SubmissionRecordCount = g.Count(r => r.RecordKind == "Submission"),
                    ClaimCount = submissionIds.Count,
                    RemittanceRecordCount = g.Count(r => r.RecordKind == "Remittance"),
                    RemittanceClaimRefCount = remittanceIds.Count,
                    Matched = submissionIds.Count(id => remittanceIds.Contains(id)),
                    UnmatchedSubmissions = submissionIds.Count(id => !remittanceIds.Contains(id)),
                    UnmatchedRemittances = remittanceIds.Count(id => !submissionIds.Contains(id))
                };
            });

        var facilityMap = facilities.ToDictionary(f => f.Id, f => f.Name);
        var allFacilityIds = txCounts.Select(x => x.FacilityId)
            .Union(parsedByFacility.Keys)
            .Distinct()
            .ToList();

        var rows = allFacilityIds.Select(fid =>
        {
            var tx = txCounts.FirstOrDefault(x => x.FacilityId == fid);
            parsedByFacility.TryGetValue(fid, out var parsed);
            var parsedSubmissionFiles = parsed?.SubmissionFileCount ?? 0;
            var parsedRemittanceFiles = parsed?.RemittanceFileCount ?? 0;

            return new XmlParsingFacilityRow
            {
                FacilityId = fid,
                FacilityName = facilityMap.GetValueOrDefault(fid, $"Facility {fid}"),
                SubmissionTotal = parsedSubmissionFiles > 0 ? parsedSubmissionFiles : tx?.SubmissionTotal ?? 0,
                SubmissionDownloaded = parsedSubmissionFiles > 0 ? parsedSubmissionFiles : tx?.SubmissionDownloaded ?? 0,
                RemittanceTotal = parsedRemittanceFiles > 0 ? parsedRemittanceFiles : tx?.RemittanceTotal ?? 0,
                RemittanceDownloaded = parsedRemittanceFiles > 0 ? parsedRemittanceFiles : tx?.RemittanceDownloaded ?? 0,
                RemittanceRecordCount = parsed?.RemittanceRecordCount ?? 0,
                RemittanceClaimRefCount = parsed?.RemittanceClaimRefCount ?? 0,
                SubmissionRecordCount = parsed?.SubmissionRecordCount ?? 0,
                Matched = parsed?.Matched ?? 0,
                UnmatchedSubmissions = parsed?.UnmatchedSubmissions ?? 0,
                UnmatchedRemittances = parsed?.UnmatchedRemittances ?? 0,
                ClaimCount = parsed?.ClaimCount ?? 0,
            };
        })
        .OrderBy(r => r.FacilityName)
        .ToList();

        var recordQuery = _db.PortalTransactions
            .Include(t => t.Facility)
            .AsNoTracking()
            .Where(t => t.Portal == "DHA");

        if (facilityIds?.Count > 0)
            recordQuery = recordQuery.Where(t => facilityIds.Contains(t.FacilityId));
        if (!string.IsNullOrWhiteSpace(search))
            recordQuery = recordQuery.Where(t =>
                t.TransactionId.Contains(search)
                || (t.FileId != null && t.FileId.Contains(search))
                || (t.FileName != null && t.FileName.Contains(search))
                || (t.Payer != null && t.Payer.Contains(search))
                || t.Status.Contains(search));
        if (string.Equals(kind, "Submission", StringComparison.OrdinalIgnoreCase))
            recordQuery = recordQuery.Where(t =>
                _db.XmlParsedRecords.Any(r => r.PortalTransactionId == t.Id && r.RecordKind == "Submission")
                || (t.Type.Contains("Claim")
                    || (t.FileName != null && (t.FileName.StartsWith("CLM") || t.FileName.Contains("Claim")))));
        else if (string.Equals(kind, "Remittance", StringComparison.OrdinalIgnoreCase))
            recordQuery = recordQuery.Where(t =>
                _db.XmlParsedRecords.Any(r => r.PortalTransactionId == t.Id && r.RecordKind == "Remittance")
                || t.Type.Contains("Remittance")
                || (t.FileName != null && (t.FileName.StartsWith("RA_") || t.FileName.Contains("Remittance"))));

        var recordItems = await recordQuery
            .OrderByDescending(t => t.SyncedAt)
            .Select(t => new
            {
                t.Id,
                t.FacilityId,
                FacilityName = t.Facility.Name,
                t.TransactionId,
                t.Type,
                t.Direction,
                t.Status,
                t.FileId,
                t.FileName,
                t.TransactionDate,
                t.Payer,
                t.Amount,
                t.FileDownloaded,
                HasXml = t.FileContentXml != null && t.FileContentXml.Length > 10,
                t.SyncedAt
            })
            .Take(200)
            .ToListAsync();

        var txIds = recordItems.Select(t => t.Id).ToList();
        var parsedGroups = await _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => txIds.Contains(r.PortalTransactionId))
            .GroupBy(r => r.PortalTransactionId)
            .Select(g => new
            {
                PortalTransactionId = g.Key,
                ParsedRows = g.Count(),
                ReadyRows = g.Count(r => r.ReadyForReport),
                SubmissionRows = g.Count(r => r.RecordKind == "Submission"),
                RemittanceRows = g.Count(r => r.RecordKind == "Remittance"),
                MatchedRows = g.Count(r => r.IsMatched),
                SampleClaimId = g.Select(r => r.ClaimId).FirstOrDefault(),
                ParsedAt = g.Max(r => (DateTime?)r.ParsedAt)
            })
            .ToListAsync();
        var parsedMap = parsedGroups.ToDictionary(x => x.PortalTransactionId);

        var parsedRecordQuery = _db.XmlParsedRecords
            .Include(r => r.Facility)
            .AsNoTracking();

        if (facilityIds?.Count > 0)
            parsedRecordQuery = parsedRecordQuery.Where(r => facilityIds.Contains(r.FacilityId));
        if (string.Equals(kind, "Submission", StringComparison.OrdinalIgnoreCase))
            parsedRecordQuery = parsedRecordQuery.Where(r => r.RecordKind == "Submission");
        else if (string.Equals(kind, "Remittance", StringComparison.OrdinalIgnoreCase))
            parsedRecordQuery = parsedRecordQuery.Where(r => r.RecordKind == "Remittance");
        if (!string.IsNullOrWhiteSpace(search))
            parsedRecordQuery = parsedRecordQuery.Where(r =>
                r.ClaimId.Contains(search)
                || (r.FileName != null && r.FileName.Contains(search))
                || (r.FileId != null && r.FileId.Contains(search))
                || (r.PaymentReference != null && r.PaymentReference.Contains(search))
                || (r.SenderId != null && r.SenderId.Contains(search))
                || (r.ReceiverId != null && r.ReceiverId.Contains(search))
                || (r.PayerName != null && r.PayerName.Contains(search))
                || (r.Comments != null && r.Comments.Contains(search)));

        var parsedRecordTotal = await parsedRecordQuery.CountAsync();
        var parsedRecordItems = await parsedRecordQuery
            .OrderByDescending(r => r.ParsedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id,
                r.PortalTransactionId,
                r.FacilityId,
                FacilityName = r.Facility!.Name,
                r.RecordKind,
                r.ClaimId,
                r.FileName,
                r.FileId,
                r.TransactionDate,
                r.SenderId,
                r.ReceiverId,
                r.PayerName,
                r.NetAmount,
                r.PaidAmount,
                r.SettlementDate,
                r.PaymentReference,
                r.DenialCodesJson,
                r.Comments,
                r.IsMatched,
                r.ReadyForReport,
                r.ParsedAt
            })
            .Take(500)
            .ToListAsync();

        return new XmlParsingViewModel
        {
            Facilities = facilities.Select(f => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(f.Name, f.Id.ToString())).ToList(),
            FacilityIds = facilityIds ?? new(),
            SearchText = search,
            Kind = kind ?? "All",
            FacilityRows = rows,
            ParsedRecordTotal = parsedRecordTotal,
            Records = recordItems.Select(t =>
            {
                parsedMap.TryGetValue(t.Id, out var parsed);
                return new XmlParsingRecordRow
                {
                    TransactionDbId = t.Id,
                    FacilityId = t.FacilityId,
                    FacilityName = t.FacilityName,
                    TransactionId = t.TransactionId,
                    Type = t.Type,
                    Direction = t.Direction,
                    Status = t.Status,
                    FileId = t.FileId,
                    FileName = t.FileName,
                    TransactionDate = t.TransactionDate,
                    Payer = t.Payer,
                    Amount = t.Amount,
                    FileDownloaded = t.FileDownloaded,
                    HasXml = t.HasXml,
                    SyncedAt = t.SyncedAt,
                    ParsedRows = parsed?.ParsedRows ?? 0,
                    ReadyRows = parsed?.ReadyRows ?? 0,
                    SubmissionRows = parsed?.SubmissionRows ?? 0,
                    RemittanceRows = parsed?.RemittanceRows ?? 0,
                    MatchedRows = parsed?.MatchedRows ?? 0,
                    SampleClaimId = parsed?.SampleClaimId ?? "",
                    ParsedAt = parsed?.ParsedAt
                };
            }).ToList(),
            ParsedRecords = parsedRecordItems.Select(r => new XmlParsingParsedRecordRow
            {
                Id = r.Id,
                PortalTransactionId = r.PortalTransactionId,
                FacilityId = r.FacilityId,
                FacilityName = r.FacilityName,
                RecordKind = r.RecordKind,
                ClaimId = r.ClaimId,
                FileName = r.FileName,
                FileId = r.FileId,
                TransactionDate = r.TransactionDate,
                SenderId = r.SenderId,
                ReceiverId = r.ReceiverId,
                PayerName = r.PayerName,
                NetAmount = r.NetAmount,
                PaidAmount = r.PaidAmount,
                SettlementDate = r.SettlementDate,
                PaymentReference = r.PaymentReference,
                DenialCodes = FormatDenialCodes(r.DenialCodesJson),
                Comments = r.Comments,
                IsMatched = r.IsMatched,
                ReadyForReport = r.ReadyForReport,
                ParsedAt = r.ParsedAt
            }).ToList()
        };
    }

    private static string FormatDenialCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(json);
            return string.Join(" | ", codes?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct() ?? []);
        }
        catch
        {
            return json;
        }
    }

    public async Task<ReconciliationViewModel> GetReconciliationAsync(
        List<int>? facilityIds, string? dateFrom, string? dateTo, List<string>? statusFilters)
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        // Load all DHA transactions that have downloaded XML content
        var query = _db.PortalTransactions
            .Where(t => t.Portal == "DHA" && t.FileContentXml != null && t.FileContentXml.Length > 10);

        if (facilityIds != null && facilityIds.Count > 0)
            query = query.Where(t => facilityIds.Contains(t.FacilityId));
        if (!string.IsNullOrEmpty(dateFrom)) query = query.Where(t => string.Compare(t.TransactionDate, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo)) query = query.Where(t => string.Compare(t.TransactionDate, dateTo) <= 0);

        // Only fetch columns needed for parsing — avoids loading large RawXml into memory
        var transactions = await query
            .Select(t => new PortalTransaction
            {
                TransactionId = t.TransactionId,
                FileId = t.FileId,
                Type = t.Type,
                FileName = t.FileName,
                FileContentXml = t.FileContentXml,
                FacilityId = t.FacilityId
            })
            .ToListAsync();   // no cap — unlimited records

        // Parse claims and remittances from XML content
        var claimMap = new Dictionary<string, ClaimEntry>(StringComparer.OrdinalIgnoreCase);
        var remittanceMap = new Dictionary<string, RemittanceEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var tx in transactions)
        {
            // Check remittance FIRST — the stored Type column is unreliable (defaults to "Claim")
            // so we always let XML content decide.
            if (IsRemittanceFile(tx))
            {
                foreach (var r in ParseRemittanceXml(tx))
                    remittanceMap.TryAdd(r.ClaimId, r);
            }
            else if (IsClaimFile(tx))
            {
                foreach (var c in ParseClaimXml(tx))
                    claimMap.TryAdd(c.ClaimId, c);
            }
        }

        // Join on ClaimID
        var allIds = claimMap.Keys.Union(remittanceMap.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<ReconciliationRow>();

        foreach (var id in allIds)
        {
            claimMap.TryGetValue(id, out var claim);
            remittanceMap.TryGetValue(id, out var remit);

            var status = DetermineStatus(claim, remit);

            rows.Add(new ReconciliationRow
            {
                ClaimId = id,
                Payer = claim?.Payer ?? remit?.Payer,
                ServiceDate = claim?.ServiceDate,
                SubmittedAmount = claim?.SubmittedAmount,
                RemittanceDate = remit?.RemittanceDate,
                PaidAmount = remit?.PaidAmount,
                PaymentStatus = status,
                ClaimFileId = claim?.SourceFileId,
                RemittanceFileId = remit?.SourceFileId,
                FacilityId = facilityIds?.Count == 1 ? facilityIds[0] : 0
            });
        }

        if (statusFilters != null && statusFilters.Count > 0)
            rows = rows.Where(r => statusFilters.Contains(r.PaymentStatus)).ToList();

        rows = rows.OrderBy(r => r.PaymentStatus == "Rejected" ? 0
                               : r.PaymentStatus == "Partial" ? 1
                               : r.PaymentStatus == "Pending" ? 2
                               : 3)
                   .ThenBy(r => r.ServiceDate)
                   .ToList();

        return new ReconciliationViewModel
        {
            Facilities = facilities.Select(f => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(f.Name, f.Id.ToString())).ToList(),
            FacilityIds = facilityIds ?? new(),
            StatusFilters = statusFilters ?? new(),
            DateFrom = dateFrom,
            DateTo = dateTo,
            TotalRowCount = rows.Count,
            Rows = rows   // unlimited — no display cap
        };
    }

    // ── File type detection ──────────────────────────────────────────

    private static bool IsClaimFile(PortalTransaction tx) =>
        tx.Type?.Contains("Claim", StringComparison.OrdinalIgnoreCase) == true
        || tx.FileName?.StartsWith("CLM", StringComparison.OrdinalIgnoreCase) == true
        || (tx.FileContentXml != null
            && (tx.FileContentXml.Contains("<Claim ") || tx.FileContentXml.Contains("<Claim>")
             || tx.FileContentXml.Contains("<Claims>") || tx.FileContentXml.Contains("Claim.Submission")));

    private static bool IsRemittanceFile(PortalTransaction tx) =>
        // Content check first — stored Type column defaults to "Claim" even for remittance files
        (tx.FileContentXml != null && (
            tx.FileContentXml.Contains("<Remittance.Advice") ||
            tx.FileContentXml.Contains("<Remittance ")))
        || tx.Type?.Contains("Remittance", StringComparison.OrdinalIgnoreCase) == true
        || tx.FileName?.StartsWith("RMT", StringComparison.OrdinalIgnoreCase) == true
        || tx.FileName?.StartsWith("RA_", StringComparison.OrdinalIgnoreCase) == true;

    // ── XML parsers ──────────────────────────────────────────────────

    private static IEnumerable<ClaimEntry> ParseClaimXml(PortalTransaction tx)
    {
        if (string.IsNullOrEmpty(tx.FileContentXml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(tx.FileContentXml); } catch { yield break; }

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Claim").Take(5000))
        {
            // DHA Claim.Submission format uses child elements, not attributes
            var claimId = Val(el, "ID", "ClaimID", "ClaimId", "id");
            if (string.IsNullOrWhiteSpace(claimId)) continue;

            // DHA uses <Gross> for submitted amount; also check <Net>, <TotalAmount>
            decimal.TryParse(Val(el, "Gross", "GrossAmount", "TotalAmount", "Net", "Amount", "NetAmount"),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount);

            // Service date: prefer <Encounter><Start>, else <Start>, else header TransactionDate
            var serviceDate = el.Element("Encounter")?.Element("Start")?.Value
                           ?? Val(el, "Start", "ServiceDate", "Date", "EncounterDate");
            if (string.IsNullOrWhiteSpace(serviceDate))
                serviceDate = Val(doc.Root?.Element("Header"), "TransactionDate");

            yield return new ClaimEntry
            {
                ClaimId = claimId,
                Payer = Val(el, "PayerID", "InsuranceCompanyID", "Payer", "ReceiverID"),
                ServiceDate = serviceDate,
                SubmittedAmount = amount > 0 ? amount : null,
                SourceFileId = tx.FileId ?? tx.TransactionId
            };
        }
    }

    private static IEnumerable<RemittanceEntry> ParseRemittanceXml(PortalTransaction tx)
    {
        if (string.IsNullOrEmpty(tx.FileContentXml)) yield break;
        XDocument doc;
        try { doc = XDocument.Parse(tx.FileContentXml); } catch { yield break; }

        var header = doc.Root?.Element("Header");
        var rootDate = Val(header, "TransactionDate", "RemittanceDate", "Date")
                     ?? (doc.Root != null ? Attr(doc.Root, "TransactionDate", "RemittanceDate", "Date") : null);
        var rootPayer = Val(header, "SenderID", "PayerID", "Payer")
                     ?? (doc.Root != null ? Attr(doc.Root, "PayerID", "Payer", "SenderID") : null);

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Claim").Take(5000))
        {
            var claimId = Val(el, "ID", "ClaimID", "ClaimId", "id");
            if (string.IsNullOrWhiteSpace(claimId)) continue;

            // DHA Remittance.Advice: paid amount is the sum of <Activity><PaymentAmount>
            // Fall back to <Net>, <Gross>, <PaidAmount> for other formats.
            decimal paid = 0;
            var activities = el.Elements().Where(e => e.Name.LocalName == "Activity").ToList();
            if (activities.Count > 0)
            {
                foreach (var act in activities)
                {
                    if (decimal.TryParse(Val(act, "PaymentAmount", "Net", "PaidAmount", "Amount"),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var actPaid))
                        paid += actPaid;
                }
            }
            else
            {
                decimal.TryParse(Val(el, "Net", "PaidAmount", "Gross", "NetAmount", "Amount", "GrossAmount"),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out paid);
            }

            // DHA uses <DateSettlement> as the settlement/remittance date
            var remitDate = Val(el, "DateSettlement", "RemittanceDate", "Date") ?? rootDate;

            yield return new RemittanceEntry
            {
                ClaimId = claimId,
                PaidAmount = paid,
                RemittanceDate = remitDate,
                PaymentStatus = Val(el, "Status", "PaymentStatus", "ClaimStatus", "Comments") ?? "Unknown",
                Payer = Val(el, "IDPayer", "PayerID", "Payer") ?? rootPayer,
                SourceFileId = tx.FileId ?? tx.TransactionId
            };
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Read value from XML attribute (for attribute-based formats).</summary>
    private static string? Attr(XElement el, params string[] names)
    {
        foreach (var n in names)
        {
            var v = el.Attribute(n)?.Value;
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    /// <summary>Read value from direct child element text (for DHA element-based format).</summary>
    private static string? Val(XElement? el, params string[] names)
    {
        if (el == null) return null;
        foreach (var n in names)
        {
            // Check direct child element
            var v = el.Element(n)?.Value;
            if (!string.IsNullOrWhiteSpace(v)) return v;
            // Fallback: check attribute too
            v = el.Attribute(n)?.Value;
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string DetermineStatus(ClaimEntry? claim, RemittanceEntry? remit)
    {
        if (remit == null) return "Pending";
        var paid = remit.PaidAmount ?? 0;
        var submitted = claim?.SubmittedAmount ?? 0;

        if (paid <= 0) return "Rejected";
        if (submitted > 0 && paid < submitted - 0.01m) return "Partial";
        return "Paid";
    }

    // ── Internal DTOs ─────────────────────────────────────────────────

    private class ClaimEntry
    {
        public string ClaimId { get; set; } = string.Empty;
        public string? Payer { get; set; }
        public string? ServiceDate { get; set; }
        public decimal? SubmittedAmount { get; set; }
        public string? SourceFileId { get; set; }
    }

    private class RemittanceEntry
    {
        public string ClaimId { get; set; } = string.Empty;
        public decimal? PaidAmount { get; set; }
        public string? RemittanceDate { get; set; }
        public string? PaymentStatus { get; set; }
        public string? Payer { get; set; }
        public string? SourceFileId { get; set; }
    }
}
