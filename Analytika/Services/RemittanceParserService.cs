using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Xml.Linq;

namespace Analytika.Services;

public class RemittanceParserService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RemittanceParserService> _logger;

    public RemittanceParserService(AppDbContext db, ILogger<RemittanceParserService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Parses all downloaded remittance XMLs that haven't been parsed yet.
    /// Returns (parsed, skipped, errors).
    /// </summary>
    public async Task<(int Parsed, int Skipped, int Errors)> ParsePendingAsync(int? facilityId = null)
    {
        // Use NOT EXISTS at database level instead of loading HashSet into memory
        var query = _db.PortalTransactions
            .Where(pt => pt.Type == "Remittance"
                      && pt.FileDownloaded
                      && pt.FileContentXml != null && pt.FileContentXml != "")
            .Where(pt => !_db.RemittanceClaims.Any(rc => rc.RemittanceTransactionId == pt.Id));

        if (facilityId.HasValue)
            query = query.Where(pt => pt.FacilityId == facilityId.Value);

        var transactions = await query
            .AsNoTracking()
            .ToListAsync();

        int parsed = 0, skipped = 0, errors = 0;

        foreach (var tx in transactions)
        {
            try
            {
                var claims = ParseXml(tx);
                if (claims.Count == 0) { skipped++; continue; }

                // RemittanceClaims still supports one row per remittance transaction for the
                // resubmission workspace. Claim Summary uses XmlParsedRecords for claim-level RA.
                var first = claims[0];
                var rc = new RemittanceClaim
                {
                    RemittanceTransactionId = tx.Id,
                    FacilityId = tx.FacilityId,
                    ClaimId = first.ClaimId,
                    PayerClaimId = first.PayerClaimId,
                    PayerCode = first.PayerCode,
                    ClinicianLicense = first.ClinicianLicense,
                    OriginalAmount = claims.Sum(c => c.OriginalAmount),
                    PaidAmount = claims.Sum(c => c.PaidAmount),
                    DenialCodesJson = JsonSerializer.Serialize(
                        claims.SelectMany(c => c.DenialCodes).Distinct().OrderBy(x => x).ToList()),
                    Comments = string.Join(" | ", claims
                        .Select(c => c.Comments)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct()),
                    ActivityCount = claims.Sum(c => c.ActivityCount),
                    SettlementDate = first.SettlementDate,
                    PaymentReference = first.PaymentReference,
                    ParsedAt = DateTime.UtcNow
                };

                _db.RemittanceClaims.Add(rc);
                parsed++;

                if (parsed % 100 == 0)
                    await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse remittance TX {Id}", tx.Id);
                errors++;
            }
        }

        if (parsed % 100 != 0)
            await _db.SaveChangesAsync();

        return (parsed, skipped, errors);
    }

    // ── XML parsing ──────────────────────────────────────────────────────────

    private record ClaimData(
        string ClaimId, string? PayerClaimId, string? PayerCode, string? ClinicianLicense,
        decimal OriginalAmount, decimal PaidAmount, List<string> DenialCodes,
        string? Comments, int ActivityCount, string? SettlementDate, string? PaymentReference);

    private static List<ClaimData> ParseXml(PortalTransaction tx)
    {
        var xml = XDocument.Parse(tx.FileContentXml!);
        var ns = xml.Root?.Name.Namespace ?? XNamespace.None;

        var senderIdEl = xml.Descendants(ns + "SenderID").FirstOrDefault()
                      ?? xml.Descendants("SenderID").FirstOrDefault();
        var payerCode = senderIdEl?.Value?.Trim();

        var results = new List<ClaimData>();

        var claimEls = xml.Descendants().Where(e => e.Name.LocalName == "Claim");

        foreach (var claimEl in claimEls)
        {
            string? V(string tag) =>
                (claimEl.Element(ns + tag) ?? claimEl.Element(tag))?.Value?.Trim();

            var claimId = V("ID") ?? "";
            var payerClId = V("IDPayer");
            var settlement = V("DateSettlement");
            var payRef = V("PaymentReference");
            var comments = V("Comments");

            // Activities (line items)
            var actEls = claimEl.Descendants().Where(e => e.Name.LocalName == "Activity").ToList();

            decimal net = 0, paid = 0;
            var denials = new List<string>();
            string? clin = null;

            foreach (var act in actEls)
            {
                string? AV(string tag) =>
                    (act.Element(ns + tag) ?? act.Element(tag))?.Value?.Trim();

                if (decimal.TryParse(AV("Net"), out var n)) net += n;
                if (decimal.TryParse(AV("PaymentAmount"), out var p)) paid += p;

                var dc = AV("DenialCode");
                if (!string.IsNullOrWhiteSpace(dc) && !denials.Contains(dc))
                    denials.Add(dc);

                clin ??= AV("Clinician");
            }

            if (string.IsNullOrWhiteSpace(claimId)) continue;

            results.Add(new ClaimData(
                ClaimId: claimId,
                PayerClaimId: payerClId,
                PayerCode: payerCode,
                ClinicianLicense: clin,
                OriginalAmount: net,
                PaidAmount: paid,
                DenialCodes: denials,
                Comments: comments,
                ActivityCount: actEls.Count,
                SettlementDate: settlement,
                PaymentReference: payRef));
        }

        return results;
    }
}
