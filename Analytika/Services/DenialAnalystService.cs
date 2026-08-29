using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using Analytika.Models;

namespace Analytika.Services;

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public class DenialAnalystViewModel
{
    public long TotalActivities { get; set; }
    public long DeniedActivities { get; set; }
    public decimal DeniedAmount { get; set; }          // Net - Payment on denied lines
    public decimal TotalNet { get; set; }
    public double DenialRatePct => TotalActivities > 0 ? Math.Round(DeniedActivities * 100.0 / TotalActivities, 1) : 0;
    public double AmountAtRiskPct => TotalNet > 0 ? Math.Round((double)(DeniedAmount * 100 / TotalNet), 1) : 0;

    public List<DenialPatternRow> TopCodes { get; set; } = new();
    public List<PayerHotspotRow> PayerHotspots { get; set; } = new();
    public List<ClinicianDenialRow> Clinicians { get; set; } = new();
    public List<ActivityDenialRow> ActivityCodes { get; set; } = new();
    public List<MonthlyDenialRow> Monthly { get; set; } = new();
    public DateTime BuiltAt { get; set; }
}

public class DenialPatternRow
{
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public long Count { get; set; }
    public decimal Amount { get; set; }
    public double SharePct { get; set; }
}

public class PayerHotspotRow
{
    public string Payer { get; set; } = "";
    public string Code { get; set; } = "";
    public long Count { get; set; }
    public decimal Amount { get; set; }
}

public class ClinicianDenialRow
{
    public string Clinician { get; set; } = "";
    public long Denied { get; set; }
    public long Total { get; set; }
    public decimal Amount { get; set; }
    public double RatePct => Total > 0 ? Math.Round(Denied * 100.0 / Total, 1) : 0;
}

public class ActivityDenialRow
{
    public string ActivityCode { get; set; } = "";
    public long Denied { get; set; }
    public decimal Amount { get; set; }
    public string? TopDenialCode { get; set; }
}

public class MonthlyDenialRow
{
    public string Month { get; set; } = "";   // MM/yyyy
    public long Total { get; set; }
    public long Denied { get; set; }
    public double RatePct => Total > 0 ? Math.Round(Denied * 100.0 / Total, 1) : 0;
}

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Resubmission Analyst — mines the parsed remittance activities for denial
/// patterns (codes, payers, clinicians, activity codes, monthly trend) and can
/// narrate the findings through the configured chat model.
/// All aggregation is server-side SQL over the skinny XmlParsedActivities /
/// XmlParsedRecords tables (never the blob table) and cached 15 minutes.
/// </summary>
public class DenialAnalystService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILlmChatClient _llm;
    private readonly ILogger<DenialAnalystService> _logger;

    public DenialAnalystService(AppDbContext db, IMemoryCache cache, ILlmChatClient llm, ILogger<DenialAnalystService> logger)
    {
        _db = db;
        _cache = cache;
        _llm = llm;
        _logger = logger;
    }

    private static string CacheKey(List<int>? fac) =>
        "denial-analyst:" + (fac == null ? "all" : string.Join(",", fac.OrderBy(x => x)));

    public async Task<DenialAnalystViewModel> BuildAsync(List<int>? allowedFacilities, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<DenialAnalystViewModel>(CacheKey(allowedFacilities), out var cached) && cached != null)
            return cached;

        var vm = new DenialAnalystViewModel { BuiltAt = DateTime.UtcNow };
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var facFilter = allowedFacilities is { Count: > 0 }
            ? $" AND r.\"FacilityId\" IN ({string.Join(",", allowedFacilities)})"
            : "";
        // Denied = remittance activity carrying a denial code.
        var baseJoin = @"FROM ""XmlParsedActivities"" a
                         JOIN ""XmlParsedRecords"" r ON r.""Id"" = a.""XmlParsedRecordId""
                         WHERE r.""RecordKind"" = 'Remittance'" + facFilter;
        var denied = @" AND a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> ''";

        async Task<List<T>> QueryAsync<T>(string sql, Func<System.Data.Common.DbDataReader, T> map)
        {
            var list = new List<T>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct)) list.Add(map(rdr));
            return list;
        }

        // KPIs — one pass.
        var kpi = await QueryAsync(
            $@"SELECT COUNT(*),
                      SUM(CASE WHEN a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> '' THEN 1 ELSE 0 END),
                      COALESCE(SUM(CASE WHEN a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> '' THEN a.""Net"" - a.""PaymentAmount"" ELSE 0 END), 0),
                      COALESCE(SUM(a.""Net""), 0)
               {baseJoin}",
            r => (Total: r.GetInt64(0), Denied: r.GetInt64(1), Amount: r.GetDecimal(2), Net: r.GetDecimal(3)));
        if (kpi.Count > 0)
        {
            vm.TotalActivities = kpi[0].Total;
            vm.DeniedActivities = kpi[0].Denied;
            vm.DeniedAmount = Math.Round(kpi[0].Amount, 2);
            vm.TotalNet = Math.Round(kpi[0].Net, 2);
        }

        // Denial-code descriptions (coding set may or may not carry a DenialCode category).
        var codeNames = await _db.DhpoCodingSets.AsNoTracking()
            .Where(c => c.Category == "DenialCode")
            .ToDictionaryAsync(c => c.Code, c => c.Name, StringComparer.OrdinalIgnoreCase, ct);

        vm.TopCodes = await QueryAsync(
            $@"SELECT a.""DenialCode"", COUNT(*), COALESCE(SUM(a.""Net"" - a.""PaymentAmount""),0)
               {baseJoin}{denied}
               GROUP BY a.""DenialCode"" ORDER BY 2 DESC LIMIT 10",
            r => new DenialPatternRow { Code = r.GetString(0), Count = r.GetInt64(1), Amount = Math.Round(r.GetDecimal(2), 2) });
        foreach (var c in vm.TopCodes)
        {
            c.Description = codeNames.GetValueOrDefault(c.Code);
            c.SharePct = vm.DeniedActivities > 0 ? Math.Round(c.Count * 100.0 / vm.DeniedActivities, 1) : 0;
        }

        vm.PayerHotspots = await QueryAsync(
            $@"SELECT COALESCE(NULLIF(r.""PayerName"",''), r.""PayerId"", 'Unknown'), a.""DenialCode"", COUNT(*), COALESCE(SUM(a.""Net"" - a.""PaymentAmount""),0)
               {baseJoin}{denied}
               GROUP BY 1, 2 ORDER BY 4 DESC LIMIT 12",
            r => new PayerHotspotRow { Payer = r.GetString(0), Code = r.GetString(1), Count = r.GetInt64(2), Amount = Math.Round(r.GetDecimal(3), 2) });

        vm.Clinicians = await QueryAsync(
            $@"SELECT COALESCE(NULLIF(a.""Clinician"",''),'Unknown'),
                      SUM(CASE WHEN a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> '' THEN 1 ELSE 0 END) AS d,
                      COUNT(*),
                      COALESCE(SUM(CASE WHEN a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> '' THEN a.""Net"" - a.""PaymentAmount"" ELSE 0 END),0)
               {baseJoin}
               GROUP BY 1 HAVING d > 0 ORDER BY 4 DESC LIMIT 10",
            r => new ClinicianDenialRow { Clinician = r.GetString(0), Denied = r.GetInt64(1), Total = r.GetInt64(2), Amount = Math.Round(r.GetDecimal(3), 2) });

        vm.ActivityCodes = await QueryAsync(
            $@"SELECT COALESCE(NULLIF(a.""ActivityCode"",''),'Unknown'), COUNT(*), COALESCE(SUM(a.""Net"" - a.""PaymentAmount""),0),
                      (SELECT a2.""DenialCode"" FROM ""XmlParsedActivities"" a2
                        JOIN ""XmlParsedRecords"" r2 ON r2.""Id"" = a2.""XmlParsedRecordId""
                        WHERE r2.""RecordKind""='Remittance' AND a2.""ActivityCode"" = a.""ActivityCode""
                          AND a2.""DenialCode"" IS NOT NULL AND a2.""DenialCode"" <> ''
                        GROUP BY a2.""DenialCode"" ORDER BY COUNT(*) DESC LIMIT 1)
               {baseJoin}{denied}
               GROUP BY 1 ORDER BY 3 DESC LIMIT 10",
            r => new ActivityDenialRow { ActivityCode = r.GetString(0), Denied = r.GetInt64(1), Amount = Math.Round(r.GetDecimal(2), 2), TopDenialCode = r.IsDBNull(3) ? null : r.GetString(3) });

        // Monthly trend — RA TransactionDate stored as dd/MM/yyyy[ HH:mm]; group by MM/yyyy.
        vm.Monthly = (await QueryAsync(
            $@"SELECT substr(r.""TransactionDate"", 4, 7),
                      COUNT(*),
                      SUM(CASE WHEN a.""DenialCode"" IS NOT NULL AND a.""DenialCode"" <> '' THEN 1 ELSE 0 END)
               {baseJoin} AND r.""TransactionDate"" IS NOT NULL AND length(r.""TransactionDate"") >= 10
               GROUP BY 1",
            r => new MonthlyDenialRow { Month = r.GetString(0), Total = r.GetInt64(1), Denied = r.GetInt64(2) }))
            .Where(m => m.Month.Length == 7)
            .OrderBy(m => m.Month.Substring(3) + m.Month.Substring(0, 2))   // yyyy then MM
            .TakeLast(12)
            .ToList();

        _cache.Set(CacheKey(allowedFacilities), vm, TimeSpan.FromMinutes(15));
        return vm;
    }

    /// <summary>
    /// Narrates the aggregates through the configured chat model. Returns plain-text
    /// bullet insights; falls back to a clear message when the model is unavailable.
    /// Cached 30 minutes per facility scope — the numbers only change on new parses.
    /// </summary>
    public async Task<string> GetAiInsightsAsync(List<int>? allowedFacilities, CancellationToken ct = default)
    {
        var key = CacheKey(allowedFacilities) + ":ai";
        if (_cache.TryGetValue<string>(key, out var cached) && !string.IsNullOrEmpty(cached))
            return cached!;

        var vm = await BuildAsync(allowedFacilities, ct);
        if (vm.DeniedActivities == 0)
            return "No denial data available yet for this scope — run a sync/parse first.";

        var sb = new StringBuilder();
        sb.AppendLine($"KPIs: activities={vm.TotalActivities:N0}, denied={vm.DeniedActivities:N0} ({vm.DenialRatePct}%), amount at risk AED {vm.DeniedAmount:N0} ({vm.AmountAtRiskPct}% of net billed).");
        sb.AppendLine("Top denial codes (code | share% | AED at risk):");
        foreach (var c in vm.TopCodes.Take(6)) sb.AppendLine($"  {c.Code} {(c.Description != null ? $"({c.Description})" : "")} | {c.SharePct}% | {c.Amount:N0}");
        sb.AppendLine("Payer x denial hotspots (payer | code | count | AED):");
        foreach (var p in vm.PayerHotspots.Take(6)) sb.AppendLine($"  {p.Payer} | {p.Code} | {p.Count:N0} | {p.Amount:N0}");
        sb.AppendLine("Clinician denial rates (license | denied/total | rate% | AED):");
        foreach (var c in vm.Clinicians.Take(5)) sb.AppendLine($"  {c.Clinician} | {c.Denied:N0}/{c.Total:N0} | {c.RatePct}% | {c.Amount:N0}");
        sb.AppendLine("Monthly denial rate: " + string.Join(", ", vm.Monthly.Select(m => $"{m.Month}={m.RatePct}%")));

        const string system =
            "You are a senior UAE healthcare RCM denial-management analyst reviewing DHA remittance denial statistics. " +
            "From the aggregates provided, produce 5-8 concise, specific, actionable bullet insights: name the dominant denial patterns, " +
            "payer-specific problems, clinician outliers, trends, and concrete next actions for the resubmission team " +
            "(e.g. eligibility checks, prior-auth process, coding review, payer contract query, targeted resubmission batches). " +
            "Plain text bullets starting with '- '. No preamble, no headings, no markdown emphasis.";

        try
        {
            // Endpoint and model are owned by the chat transport (cloud provider settings);
            // this service only supplies the prompt.
            var (content, _) = await _llm.ChatAsync(system, sb.ToString(),
                formatSchema: null, temperature: 0.2, timeout: TimeSpan.FromSeconds(90), ct);

            if (!string.IsNullOrWhiteSpace(content))
            {
                _cache.Set(key, content.Trim(), TimeSpan.FromMinutes(30));
                return content.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Denial analyst insights unavailable");
        }
        return "AI analyst unavailable (local model not reachable). The pattern tables above are current.";
    }
}
