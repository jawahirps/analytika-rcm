using System.Text.Json;
using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Maintains the legacy resubmission work queue from the unified XML parser output.
/// XML parsing remains owned by XmlParsingService; this service only projects parsed
/// remittance claim rows into RemittanceClaims so existing ResubmissionTask FKs keep working.
/// </summary>
public class ResubmissionProjectionService
{
    private readonly AppDbContext _db;
    private readonly XmlParsingService _xmlParsing;
    private readonly ILogger<ResubmissionProjectionService> _logger;

    public ResubmissionProjectionService(
        AppDbContext db,
        XmlParsingService xmlParsing,
        ILogger<ResubmissionProjectionService> logger)
    {
        _db = db;
        _xmlParsing = xmlParsing;
        _logger = logger;
    }

    public async Task<ResubmissionProjectionResult> ParseXmlAndSyncAsync(int? facilityId = null, CancellationToken ct = default)
    {
        var parseResult = await _xmlParsing.ParseDownloadedXmlAsync(facilityId, rebuild: false, ct: ct);
        var projectionResult = await SyncFromParsedRemittancesAsync(facilityId, ct);

        projectionResult.XmlFilesParsed = parseResult.FilesParsed;
        projectionResult.XmlRowsSaved = parseResult.RecordsSaved;
        projectionResult.XmlRemittanceRows = parseResult.RemittanceRows;
        return projectionResult;
    }

    public async Task<ResubmissionProjectionResult> SyncFromParsedRemittancesAsync(int? facilityId = null, CancellationToken ct = default)
    {
        var parsedQuery = _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.ReadyForReport && r.RecordKind == "Remittance");

        if (facilityId.HasValue)
            parsedQuery = parsedQuery.Where(r => r.FacilityId == facilityId.Value);

        var parsedRows = await parsedQuery
            .Select(r => new
            {
                r.PortalTransactionId,
                r.FacilityId,
                r.ClaimId,
                r.IdPayer,
                r.SenderId,
                r.Clinician,
                r.NetAmount,
                r.PaidAmount,
                r.DenialCodesJson,
                r.Comments,
                r.ActivityCount,
                r.SettlementDate,
                r.PaymentReference
            })
            .ToListAsync(ct);

        var existingQuery = _db.RemittanceClaims.AsNoTracking();
        if (facilityId.HasValue)
            existingQuery = existingQuery.Where(r => r.FacilityId == facilityId.Value);

        var existing = await existingQuery
            .Select(r => new { r.RemittanceTransactionId, r.ClaimId })
            .ToListAsync(ct);

        var existingKeys = existing
            .Select(r => QueueKey(r.RemittanceTransactionId, r.ClaimId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        foreach (var row in parsedRows)
        {
            if (string.IsNullOrWhiteSpace(row.ClaimId))
                continue;

            var key = QueueKey(row.PortalTransactionId, row.ClaimId);
            if (existingKeys.Contains(key))
                continue;

            _db.RemittanceClaims.Add(new RemittanceClaim
            {
                RemittanceTransactionId = row.PortalTransactionId,
                FacilityId = row.FacilityId,
                ClaimId = row.ClaimId,
                PayerClaimId = row.IdPayer,
                PayerCode = row.SenderId,
                ClinicianLicense = row.Clinician,
                OriginalAmount = row.NetAmount,
                PaidAmount = row.PaidAmount,
                DenialCodesJson = NormalizeDenialCodes(row.DenialCodesJson),
                Comments = row.Comments,
                ActivityCount = row.ActivityCount,
                SettlementDate = row.SettlementDate,
                PaymentReference = row.PaymentReference,
                ClaimCategory = ClaimCategory(row.DenialCodesJson),
                ParsedAt = DateTime.UtcNow
            });

            existingKeys.Add(key);
            created++;

            if (created % 500 == 0)
                await _db.SaveChangesAsync(ct);
        }

        if (created % 500 != 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[ResubmissionProjection] Projected {created} new remittance claim row(s) from XmlParsedRecords", created);

        return new ResubmissionProjectionResult
        {
            ParsedRemittanceRows = parsedRows.Count,
            CreatedQueueRows = created
        };
    }

    private static string QueueKey(int transactionId, string claimId)
        => $"{transactionId}:{claimId.Trim()}";

    private static string? NormalizeDenialCodes(string? json)
    {
        var codes = ParseDenialCodes(json);
        return codes.Count == 0 ? null : JsonSerializer.Serialize(codes.OrderBy(c => c).ToList());
    }

    private static string ClaimCategory(string? json)
    {
        var categories = ParseDenialCodes(json)
            .Select(DenialCategory)
            .Where(c => c != "Other")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return categories.Count switch
        {
            0 => "Unknown",
            1 => categories[0],
            _ => "Mixed"
        };
    }

    private static string DenialCategory(string code)
    {
        var prefix = code.Split('-')[0];
        return prefix switch
        {
            "MNEC" or "CODE" or "NCOV" or "BENX" => "Medical",
            "PRCE" or "CLAI" or "AUTH" or "DUPL"
                or "ELIG" or "COPY" or "WRNG" or "SURC"
                or "TIME" => "Technical",
            _ => "Other"
        };
    }

    private static List<string> ParseDenialCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return json
                .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

public class ResubmissionProjectionResult
{
    public int XmlFilesParsed { get; set; }
    public int XmlRowsSaved { get; set; }
    public int XmlRemittanceRows { get; set; }
    public int ParsedRemittanceRows { get; set; }
    public int CreatedQueueRows { get; set; }
}
