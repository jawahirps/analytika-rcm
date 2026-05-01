using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
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

    public async Task<XmlParsingViewModel> GetXmlParsingStatsAsync(List<int>? facilityIds)
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        // Fast per-facility counts using DB-side LIKE (EF Core + SQLite translates Contains → LIKE)
        var baseQuery = _db.PortalTransactions
            .Where(t => t.Portal == "DHA" && t.FileContentXml != null && t.FileContentXml.Length > 10);

        if (facilityIds?.Count > 0)
            baseQuery = baseQuery.Where(t => facilityIds.Contains(t.FacilityId));

        // Submission counts per facility (anything NOT containing <Remittance.Advice)
        var subCounts = await baseQuery
            .Where(t => !t.FileContentXml!.Contains("<Remittance.Advice"))
            .GroupBy(t => t.FacilityId)
            .Select(g => new { FacilityId = g.Key, Total = g.Count(), Downloaded = g.Count(t => t.FileDownloaded) })
            .ToListAsync();

        // Remittance counts per facility
        var remCounts = await baseQuery
            .Where(t => t.FileContentXml!.Contains("<Remittance.Advice"))
            .GroupBy(t => t.FacilityId)
            .Select(g => new { FacilityId = g.Key, Total = g.Count(), Downloaded = g.Count(t => t.FileDownloaded) })
            .ToListAsync();

        // Compute matched count per facility using ClaimID intersection
        // Load claim IDs from submission and remittance files per facility
        var subIds = await baseQuery
            .Where(t => !t.FileContentXml!.Contains("<Remittance.Advice"))
            .Select(t => new { t.FacilityId, t.FileContentXml })
            .ToListAsync();

        var remIds = await baseQuery
            .Where(t => t.FileContentXml!.Contains("<Remittance.Advice"))
            .Select(t => new { t.FacilityId, t.FileContentXml })
            .ToListAsync();

        // Parse claim IDs per facility
        static HashSet<string> ExtractClaimIds(IEnumerable<string?> xmlList)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var xml in xmlList)
            {
                if (string.IsNullOrEmpty(xml)) continue;
                try
                {
                    var doc = XDocument.Parse(xml);
                    foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Claim"))
                    {
                        var id = el.Element("ID")?.Value ?? el.Attribute("ID")?.Value;
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                    }
                }
                catch { }
            }
            return ids;
        }

        var subByFacility = subIds.GroupBy(x => x.FacilityId)
            .ToDictionary(g => g.Key, g => ExtractClaimIds(g.Select(x => x.FileContentXml)));
        var remByFacility = remIds.GroupBy(x => x.FacilityId)
            .ToDictionary(g => g.Key, g => ExtractClaimIds(g.Select(x => x.FileContentXml)));

        // Count <Claim occurrences per facility — fast string scan, no full parse needed
        var claimCountByFacility = subIds.GroupBy(x => x.FacilityId)
            .ToDictionary(g => g.Key, g => g.Sum(x =>
            {
                if (string.IsNullOrEmpty(x.FileContentXml)) return 0;
                int n = 0, i = 0;
                while ((i = x.FileContentXml.IndexOf("<Claim", i, StringComparison.Ordinal)) >= 0) { n++; i++; }
                return n;
            }));

        var facilityMap = facilities.ToDictionary(f => f.Id, f => f.Name);
        var allFacilityIds = subCounts.Select(x => x.FacilityId)
            .Union(remCounts.Select(x => x.FacilityId)).Distinct().ToList();

        var rows = allFacilityIds.Select(fid =>
        {
            var sub = subCounts.FirstOrDefault(x => x.FacilityId == fid);
            var rem = remCounts.FirstOrDefault(x => x.FacilityId == fid);
            subByFacility.TryGetValue(fid, out var subClaimIds);
            remByFacility.TryGetValue(fid, out var remClaimIds);
            subClaimIds ??= new();
            remClaimIds ??= new();

            var matched = subClaimIds.Count(id => remClaimIds.Contains(id));

            return new XmlParsingFacilityRow
            {
                FacilityId = fid,
                FacilityName = facilityMap.GetValueOrDefault(fid, $"Facility {fid}"),
                SubmissionTotal = sub?.Total ?? 0,
                SubmissionDownloaded = sub?.Downloaded ?? 0,
                RemittanceTotal = rem?.Total ?? 0,
                RemittanceDownloaded = rem?.Downloaded ?? 0,
                Matched = matched,
                UnmatchedSubmissions = subClaimIds.Count - matched,
                UnmatchedRemittances = remClaimIds.Count(id => !subClaimIds.Contains(id)),
                ClaimCount = claimCountByFacility.GetValueOrDefault(fid, 0),
            };
        })
        .OrderBy(r => r.FacilityName)
        .ToList();

        return new XmlParsingViewModel
        {
            Facilities = facilities.Select(f => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(f.Name, f.Id.ToString())).ToList(),
            FacilityIds = facilityIds ?? new(),
            FacilityRows = rows
        };
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
