using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Analytika.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// BIX Analytics Assistant backed by a LOCAL Ollama model, following the
/// controlled intent-to-query architecture:
///
///   Ollama understands the question   (Stage A: structured intent extraction)
///   ASP.NET controls the workflow     (validation, permissions, limits, audit)
///   Approved queries calculate        (fixed parameterized read-only SQL)
///   Ollama explains the result        (Stage B: grounded natural language)
///
/// The model NEVER supplies SQL, table names, or column names — it only picks
/// an intent code and parameters, which are validated deterministically.
/// </summary>
public interface IBixAssistantService
{
    Task<bool> IsEnabledAndAvailableAsync(CancellationToken ct = default);
    Task AskStreamAsync(string question, string? userId, string? userName, int? userFacilityId,
        Func<object, Task> emit, CancellationToken ct = default);
}

public class BixAssistantService : IBixAssistantService
{
    private readonly IOllamaClient _ollama;
    private readonly AppDbContext _db;
    private readonly ILogger<BixAssistantService> _log;

    public BixAssistantService(IOllamaClient ollama, AppDbContext db, ILogger<BixAssistantService> log)
    {
        _ollama = ollama;
        _db = db;
        _log = log;
    }

    // ── Settings (SystemSettings Category='Ollama'; safe defaults) ────────────
    private sealed record OllamaSettings(bool Enabled, string BaseUrl, string Model, double Temperature, int NumCtx, int TimeoutSeconds);

    private async Task<OllamaSettings> GetSettingsAsync()
    {
        var map = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Category == "Ollama")
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        bool enabled = !map.TryGetValue("Enabled", out var en) || !bool.TryParse(en, out var eb) || eb;
        var baseUrl = map.TryGetValue("BaseUrl", out var bu) && !string.IsNullOrWhiteSpace(bu) ? bu!.Trim() : "http://localhost:11434";
        var model = map.TryGetValue("Model", out var m) && !string.IsNullOrWhiteSpace(m) ? m!.Trim() : "qwen2.5:7b-instruct";
        var temp = map.TryGetValue("Temperature", out var t) && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var td) ? td : 0.1;
        var ctx = map.TryGetValue("NumCtx", out var nc) && int.TryParse(nc, out var nci) ? nci : 8192;
        var timeout = map.TryGetValue("TimeoutSeconds", out var ts) && int.TryParse(ts, out var tsi) ? tsi : 120;
        return new OllamaSettings(enabled, baseUrl, model, temp, ctx, timeout);
    }

    public async Task<bool> IsEnabledAndAvailableAsync(CancellationToken ct = default)
    {
        var s = await GetSettingsAsync();
        return s.Enabled && await _ollama.IsAvailableAsync(s.BaseUrl, ct);
    }

    // ── Approved intent catalogue ─────────────────────────────────────────────
    // {DK}=sortable yyyyMMdd key from dd/MM/yyyy text dates; optional segments
    // {FAC}/{PAYER}/{CLIN} are appended only when the validated parameter exists.
    private const string DkTx = "substr(TransactionDate,7,4)||substr(TransactionDate,4,2)||substr(TransactionDate,1,2)";

    private sealed record IntentDef(string Code, string Description, string Sql,
        bool SupportsPayer = false, bool SupportsClinician = false, bool NeedsClaimId = false, bool SupportsLimit = false);

    private static readonly Dictionary<string, IntentDef> Intents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TOTAL_BILLED"] = new("TOTAL_BILLED", "Total billed/claimed amount and claim count (submissions) in the period",
            $"SELECT COUNT(*) AS Claims, ROUND(SUM(NetAmount),2) AS BilledAED FROM XmlParsedRecords WHERE RecordKind='Submission' AND ReadyForReport=1 AND {DkTx} BETWEEN @df AND @dt{{FAC}}"),
        ["TOTAL_PAID"] = new("TOTAL_PAID", "Total paid/collected (RA) amount and remittance count in the period",
            $"SELECT COUNT(*) AS Remittances, ROUND(SUM(PaidAmount),2) AS PaidAED FROM XmlParsedRecords WHERE RecordKind='Remittance' AND ReadyForReport=1 AND {DkTx} BETWEEN @df AND @dt{{FAC}}"),
        ["TOTAL_DENIED"] = new("TOTAL_DENIED", "Total denied/rejected value and denied claim count in the period",
            $"SELECT SUM(CASE WHEN NetAmount>PaidAmount THEN 1 ELSE 0 END) AS DeniedClaims, ROUND(SUM(CASE WHEN NetAmount>PaidAmount THEN NetAmount-PaidAmount ELSE 0 END),2) AS DeniedAED FROM XmlParsedRecords WHERE RecordKind='Remittance' AND ReadyForReport=1 AND {DkTx} BETWEEN @df AND @dt{{FAC}}"),
        ["PAYER_DENIAL_RATE"] = new("PAYER_DENIAL_RATE", "Rejection/denial rate by payer (highest first)",
            $"SELECT PayerName, COUNT(*) AS Claims, SUM(CASE WHEN NetAmount>PaidAmount THEN 1 ELSE 0 END) AS Denied, ROUND(100.0*SUM(CASE WHEN NetAmount>PaidAmount THEN 1 ELSE 0 END)/COUNT(*),2) AS DenialRatePct FROM XmlParsedRecords WHERE RecordKind='Remittance' AND ReadyForReport=1 AND PayerName IS NOT NULL AND PayerName<>'' AND {DkTx} BETWEEN @df AND @dt{{FAC}}{{PAYER}} GROUP BY PayerName HAVING COUNT(*)>=10 ORDER BY DenialRatePct DESC LIMIT @limit",
            SupportsPayer: true, SupportsLimit: true),
        ["TOP_PAYERS_BY_PAID"] = new("TOP_PAYERS_BY_PAID", "Payers ranked by paid amount",
            $"SELECT PayerName, COUNT(*) AS Remittances, ROUND(SUM(PaidAmount),2) AS PaidAED FROM XmlParsedRecords WHERE RecordKind='Remittance' AND ReadyForReport=1 AND PayerName IS NOT NULL AND PayerName<>'' AND {DkTx} BETWEEN @df AND @dt{{FAC}} GROUP BY PayerName ORDER BY PaidAED DESC LIMIT @limit",
            SupportsLimit: true),
        ["TOP_CLINICIANS_BY_BILLED"] = new("TOP_CLINICIANS_BY_BILLED", "Clinicians ranked by billed (submitted) value",
            $"SELECT Clinician, COUNT(*) AS Claims, ROUND(SUM(NetAmount),2) AS BilledAED FROM XmlParsedRecords WHERE RecordKind='Submission' AND ReadyForReport=1 AND Clinician IS NOT NULL AND Clinician<>'' AND {DkTx} BETWEEN @df AND @dt{{FAC}}{{CLIN}} GROUP BY Clinician ORDER BY BilledAED DESC LIMIT @limit",
            SupportsClinician: true, SupportsLimit: true),
        ["CLAIMS_BY_MONTH"] = new("CLAIMS_BY_MONTH", "Monthly submitted claim counts and billed value trend",
            $"SELECT substr(TransactionDate,7,4)||'-'||substr(TransactionDate,4,2) AS Month, COUNT(*) AS Claims, ROUND(SUM(NetAmount),2) AS BilledAED FROM XmlParsedRecords WHERE RecordKind='Submission' AND ReadyForReport=1 AND {DkTx} BETWEEN @df AND @dt{{FAC}} GROUP BY Month ORDER BY Month"),
        ["PAID_BY_MONTH"] = new("PAID_BY_MONTH", "Monthly paid (RA) value trend",
            $"SELECT substr(TransactionDate,7,4)||'-'||substr(TransactionDate,4,2) AS Month, COUNT(*) AS Remittances, ROUND(SUM(PaidAmount),2) AS PaidAED FROM XmlParsedRecords WHERE RecordKind='Remittance' AND ReadyForReport=1 AND {DkTx} BETWEEN @df AND @dt{{FAC}} GROUP BY Month ORDER BY Month"),
        ["FACILITY_COMPARISON"] = new("FACILITY_COMPARISON", "Facility/branch comparison: claims, billed, paid",
            $"SELECT f.Name AS Facility, SUM(CASE WHEN r.RecordKind='Submission' THEN 1 ELSE 0 END) AS Claims, ROUND(SUM(CASE WHEN r.RecordKind='Submission' THEN r.NetAmount ELSE 0 END),2) AS BilledAED, ROUND(SUM(CASE WHEN r.RecordKind='Remittance' THEN r.PaidAmount ELSE 0 END),2) AS PaidAED FROM XmlParsedRecords r JOIN Facilities f ON f.Id=r.FacilityId WHERE r.ReadyForReport=1 AND {DkTx.Replace("TransactionDate", "r.TransactionDate")} BETWEEN @df AND @dt{{FACR}} GROUP BY f.Name ORDER BY BilledAED DESC"),
        ["TOP_DENIAL_CODES"] = new("TOP_DENIAL_CODES", "Most frequent denial/rejection codes",
            $"SELECT j.value AS DenialCode, COUNT(*) AS Occurrences FROM XmlParsedRecords r, json_each(r.DenialCodesJson) j WHERE r.RecordKind='Remittance' AND r.ReadyForReport=1 AND r.DenialCodesJson LIKE '[%' AND {DkTx.Replace("TransactionDate", "r.TransactionDate")} BETWEEN @df AND @dt{{FACR}} GROUP BY j.value ORDER BY Occurrences DESC LIMIT @limit",
            SupportsLimit: true),
        ["OUTSTANDING_BY_PAYER"] = new("OUTSTANDING_BY_PAYER", "Outstanding exposure (billed minus paid) by payer",
            $"SELECT PayerName, ROUND(SUM(CASE WHEN RecordKind='Submission' THEN NetAmount ELSE 0 END),2) AS BilledAED, ROUND(SUM(CASE WHEN RecordKind='Remittance' THEN PaidAmount ELSE 0 END),2) AS PaidAED, ROUND(SUM(CASE WHEN RecordKind='Submission' THEN NetAmount ELSE 0 END)-SUM(CASE WHEN RecordKind='Remittance' THEN PaidAmount ELSE 0 END),2) AS OutstandingAED FROM XmlParsedRecords WHERE ReadyForReport=1 AND PayerName IS NOT NULL AND PayerName<>'' AND {DkTx} BETWEEN @df AND @dt{{FAC}} GROUP BY PayerName ORDER BY OutstandingAED DESC LIMIT @limit",
            SupportsLimit: true),
        ["CLAIM_STATUS_LOOKUP"] = new("CLAIM_STATUS_LOOKUP", "Status of one claim by Claim ID (submission + remittance, no patient identifiers)",
            "SELECT RecordKind, TransactionDate, ROUND(NetAmount,2) AS NetAED, ROUND(PaidAmount,2) AS PaidAED, EncounterType, PayerName, DenialCodesJson AS DenialCodes, SettlementDate FROM XmlParsedRecords WHERE ReadyForReport=1 AND ClaimId=@claimId{FAC} ORDER BY RecordKind, TransactionDate LIMIT 20",
            NeedsClaimId: true),
    };

    // ── Stage A: structured intent extraction schema ─────────────────────────
    private static readonly object IntentSchema = new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["intent"] = new { type = "string", @enum = Intents.Keys.ToArray() },
            ["dateFrom"] = new { type = new[] { "string", "null" }, description = "yyyy-MM-dd" },
            ["dateTo"] = new { type = new[] { "string", "null" }, description = "yyyy-MM-dd" },
            ["facilityName"] = new { type = new[] { "string", "null" } },
            ["payerName"] = new { type = new[] { "string", "null" } },
            ["clinicianName"] = new { type = new[] { "string", "null" } },
            ["claimId"] = new { type = new[] { "string", "null" } },
            ["limit"] = new { type = new[] { "integer", "null" } },
            ["confidence"] = new { type = "number" }
        },
        required = new[] { "intent", "confidence" }
    };

    private static string BuildStageAPrompt(DateTime today)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You classify analytics questions for BIX, a UAE healthcare revenue-cycle platform. Amounts are AED.");
        sb.AppendLine($"Today's date is {today:yyyy-MM-dd}. Resolve relative periods (last month, this year, last six months) to exact dates.");
        sb.AppendLine("Pick exactly one intent from this catalogue:");
        foreach (var i in Intents.Values) sb.AppendLine($"- {i.Code}: {i.Description}");
        sb.AppendLine("Glossary: billed/claimed=submitted Net amount; paid/collected/settled=RA Paid amount; denied/rejected=Net exceeds Paid on the remittance.");
        sb.AppendLine("If no dates are mentioned, leave dateFrom/dateTo null. Set confidence 0..1 for how well the question maps to the chosen intent.");
        sb.AppendLine("Respond ONLY with the JSON object.");
        return sb.ToString();
    }

    private const string StageBPrompt =
        "You are the BIX Analytics Assistant. Answer ONLY from the query result provided — never invent values. " +
        "Lead with the direct answer, cite the key numbers with thousands separators and 'AED' for money, " +
        "state the report period and any filters applied, and add one short insight if the data clearly shows one. " +
        "If the result is empty, say no matching data was returned for that period. " +
        "Keep it to a few sentences. Do not mention SQL, databases, or intents.";

    // ── Main flow ─────────────────────────────────────────────────────────────
    public async Task AskStreamAsync(string question, string? userId, string? userName, int? userFacilityId,
        Func<object, Task> emit, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var s = await GetSettingsAsync();
        int totalTokens = 0;
        string auditDetail = "";

        async Task Emit(object o) { try { await emit(o); } catch { } }

        try
        {
            if (string.IsNullOrWhiteSpace(question)) { await Emit(new { error = "Please enter a question." }); return; }

            // Stage A — understand the question (structured output only)
            await Emit(new { stage = "understanding" });
            var (raw, t1) = await _ollama.ChatAsync(s.BaseUrl, s.Model, BuildStageAPrompt(DateTime.Today), question,
                IntentSchema, s.Temperature, s.NumCtx, TimeSpan.FromSeconds(s.TimeoutSeconds), ct);
            totalTokens += t1;

            var parsed = ParseStageA(raw);
            if (parsed == null) { await LogAsync(userId, userName, question, "stageA-unparseable", 0, totalTokens, false, "Stage A output not parseable", s.Model, sw, ct); await Emit(new { error = "I couldn't understand that question. Try rephrasing it around claims, payments, denials, payers, clinicians or facilities." }); return; }

            // Deterministic validation + guardrails (Phase 16)
            if (!Intents.TryGetValue(parsed.Intent, out var def))
            { await Emit(new { error = $"That request doesn't map to a supported report." }); return; }
            if (parsed.Confidence < 0.60)
            { await LogAsync(userId, userName, question, $"lowconf:{parsed.Intent}:{parsed.Confidence:0.00}", 0, totalTokens, false, "confidence below threshold", s.Model, sw, ct); await Emit(new { error = "I couldn't map that question to a supported report with enough confidence. Could you rephrase it — e.g. 'total paid amount last month' or 'denial rate by payer this year'?" }); return; }

            var today = DateTime.Today;
            var from = ParseDate(parsed.DateFrom) ?? today.AddMonths(-12);
            var to = ParseDate(parsed.DateTo) ?? today;
            if (to < from) (from, to) = (to, from);
            if ((to - from).TotalDays > 1100) from = to.AddDays(-1100);   // range cap

            // Facility scope: the authenticated session wins over anything the model said (Phase 9)
            int? facilityId = userFacilityId;
            string? facilityLabel = null;
            if (facilityId == null && !string.IsNullOrWhiteSpace(parsed.FacilityName))
            {
                var fac = await _db.Facilities.AsNoTracking()
                    .Where(f => f.Name.ToLower().Contains(parsed.FacilityName.ToLower()))
                    .Select(f => new { f.Id, f.Name }).FirstOrDefaultAsync(ct);
                if (fac != null) { facilityId = fac.Id; facilityLabel = fac.Name; }
            }
            else if (facilityId != null)
                facilityLabel = await _db.Facilities.AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);

            if (def.NeedsClaimId && string.IsNullOrWhiteSpace(parsed.ClaimId))
            { await Emit(new { error = "Please include the Claim ID you want to look up." }); return; }

            var limit = Math.Clamp(parsed.Limit ?? 10, 3, 50);

            // Build final SQL from the approved template + validated optional segments
            var sql = def.Sql
                .Replace("{FACR}", facilityId.HasValue ? " AND r.FacilityId=@fac" : "")
                .Replace("{FAC}", facilityId.HasValue ? " AND FacilityId=@fac" : "")
                .Replace("{PAYER}", def.SupportsPayer && !string.IsNullOrWhiteSpace(parsed.PayerName) ? " AND PayerName LIKE @payer" : "")
                .Replace("{CLIN}", def.SupportsClinician && !string.IsNullOrWhiteSpace(parsed.ClinicianName) ? " AND Clinician LIKE @clin" : "");

            auditDetail = JsonSerializer.Serialize(new { intent = def.Code, from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd"), facilityId, parsed.PayerName, parsed.ClinicianName, parsed.ClaimId, limit });

            // Execute (read-only connection, hard caps)
            await Emit(new { stage = "querying" });
            var (cols, rows) = ExecuteReadOnly(sql, p =>
            {
                p("@df", from.ToString("yyyyMMdd")); p("@dt", to.ToString("yyyyMMdd"));
                if (facilityId.HasValue) p("@fac", facilityId.Value);
                if (def.SupportsPayer && !string.IsNullOrWhiteSpace(parsed.PayerName)) p("@payer", "%" + parsed.PayerName.Trim() + "%");
                if (def.SupportsClinician && !string.IsNullOrWhiteSpace(parsed.ClinicianName)) p("@clin", "%" + parsed.ClinicianName.Trim() + "%");
                if (def.NeedsClaimId) p("@claimId", parsed.ClaimId!.Trim());
                if (def.SupportsLimit || sql.Contains("@limit")) p("@limit", limit);
            }, ct);

            var sourceLabel = $"Report: {def.Description}\nPeriod: {from:dd MMM yyyy} – {to:dd MMM yyyy}\nFacility: {facilityLabel ?? "All facilities"}";
            await Emit(new { stage = "answering", sql = sourceLabel, columns = cols, rows, rowCount = rows.Count, redacted = true });

            // Stage B — explain the database result (grounded, streamed)
            var resultTable = RenderTable(cols, rows);
            var stageBUser = $"Question: {question}\n\n{sourceLabel}\n\nQuery result ({rows.Count} row(s)):\n{resultTable}";
            var t2 = await _ollama.ChatStreamAsync(s.BaseUrl, s.Model, StageBPrompt, stageBUser,
                s.Temperature, s.NumCtx, TimeSpan.FromSeconds(s.TimeoutSeconds),
                async tok => await Emit(new { token = tok }), ct);
            totalTokens += t2;

            await LogAsync(userId, userName, question, auditDetail, rows.Count, totalTokens, true, null, "ollama:" + s.Model, sw, ct);
            await Emit(new { done = true, tokens = totalTokens });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BIX assistant request failed");
            await LogAsync(userId, userName, question, auditDetail, 0, totalTokens, false, ex.Message, "ollama:" + s.Model, sw, ct);
            await Emit(new { error = "The local assistant hit a problem: " + ex.Message });
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private sealed class StageAResult
    {
        public string Intent { get; set; } = "";
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public string? FacilityName { get; set; }
        public string? PayerName { get; set; }
        public string? ClinicianName { get; set; }
        public string? ClaimId { get; set; }
        public int? Limit { get; set; }
        public double Confidence { get; set; }
    }

    private static StageAResult? ParseStageA(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return JsonSerializer.Deserialize<StageAResult>(raw[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    private (List<string> cols, List<List<string?>> rows) ExecuteReadOnly(string sql, Action<Action<string, object>> bind, CancellationToken ct)
    {
        var raw = _db.Database.GetDbConnection().ConnectionString;
        var csb = new SqliteConnectionStringBuilder(raw) { Mode = SqliteOpenMode.ReadOnly, Pooling = true };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA query_only=1;PRAGMA busy_timeout=15000;"; pragma.ExecuteNonQuery(); }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        bind((name, value) => { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); });

        var cols = new List<string>();
        var rows = new List<List<string?>>();
        using var reader = cmd.ExecuteReader();
        for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
        while (reader.Read() && rows.Count < 200)
        {
            ct.ThrowIfCancellationRequested();
            var row = new List<string?>(cols.Count);
            for (var i = 0; i < cols.Count; i++) row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString());
            rows.Add(row);
        }
        return (cols, rows);
    }

    private static string RenderTable(List<string> cols, List<List<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", cols));
        var max = Math.Min(rows.Count, 50);
        for (var r = 0; r < max; r++) sb.AppendLine(string.Join(" | ", rows[r].Select(v => v ?? "")));
        if (rows.Count > max) sb.AppendLine($"... ({rows.Count - max} more rows)");
        return sb.ToString();
    }

    private async Task LogAsync(string? userId, string? userName, string question, string detail,
        int rows, int tokens, bool success, string? error, string model, Stopwatch sw, CancellationToken ct)
    {
        try
        {
            _db.AiUsageLogs.Add(new AiUsageLog
            {
                UserId = userId,
                UserName = userName,
                Question = question.Length > 2000 ? question[..2000] : question,
                GeneratedSql = detail.Length > 4000 ? detail[..4000] : detail,
                RowsReturned = rows,
                TotalTokens = tokens,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                Success = success,
                ErrorMessage = error is { Length: > 1000 } ? error[..1000] : error,
                Model = model,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to write assistant audit log"); }
    }
}
