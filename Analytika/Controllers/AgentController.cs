using Analytika.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Analytika.Controllers;

/// <summary>
/// Data Analyst agent — turns natural-language questions into tabular reports
/// over the local analytics database. Uses Claude (Anthropic:ApiKey) to generate
/// SQL when configured; otherwise falls back to a deterministic intent engine so
/// the agent works out of the box.
/// </summary>
[Authorize]
public class AgentController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentController> _logger;

    private const int MaxRows = 500;
    private const int MaxPromptLength = 600;

    /* Tables + columns the agent may query. Anything else is rejected. */
    private static readonly Dictionary<string, string> SchemaDoc = new()
    {
        ["Facilities"] = "Id, Name, IsActive",
        ["PortalTransactions"] = "Id, Portal (DHA|RHA), FacilityId, TransactionId, Type, Status, Direction, FileDownloaded (0/1), FileSizeBytes, TransactionDate (text), Payer, Amount (text), SyncPeriod (YYYY-MM), SyncedAt",
        ["XmlParsedRecords"] = "Id, PortalTransactionId, FacilityId, RecordKind (Submission|Remittance), ClaimId, TransactionDate, ReceiverName, PayerId, PayerName, PatientId, MemberId, EncounterType, Clinician, ServiceYear, ServiceMonth, GrossAmount, NetAmount, PaidAmount, ActivityCount",
        ["RemittanceClaims"] = "Id, FacilityId, ClaimId, PayerClaimId, PayerCode, ClinicianLicense, OriginalAmount, PaidAmount, DenialCodesJson, ActivityCount, SettlementDate, ClaimCategory (FullyPaid|PartiallyPaid|FullyDenied|Unknown)",
        ["ResubmissionTasks"] = "Id, RemittanceClaimId, Status, AssignedToUserId, CreatedAt",
        ["Payers"] = "Id, Code, Name",
        ["Clinicians"] = "Id, License, Name",
        ["PortalFetchLogs"] = "Id, FacilityId, Portal, Operation, Status, Message, CreatedAt",
    };

    private static readonly Regex ForbiddenSql = new(
        @"\b(insert|update|delete|drop|alter|create|attach|detach|pragma|vacuum|reindex|replace|transaction|commit|rollback)\b|;.*\S",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public AgentController(AppDbContext db, IHttpClientFactory http, IConfiguration config, ILogger<AgentController> logger)
    {
        _db = db;
        _http = http;
        _config = config;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Query([FromBody] AgentQueryRequest req)
    {
        var prompt = (req?.Prompt ?? "").Trim();
        if (prompt.Length == 0)
            return BadRequest(new { error = "Ask a question about your data." });
        if (prompt.Length > MaxPromptLength)
            prompt = prompt[..MaxPromptLength];

        var apiKey = _config["Anthropic:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var llm = await RunClaudeQueryAsync(apiKey, prompt);
                if (llm != null) return Ok(llm);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Claude agent path failed; falling back to built-in reports");
            }
        }

        var result = await RunBuiltInReportAsync(prompt);
        return Ok(result);
    }

    /* ── LLM path: Claude generates a single SELECT ─────────────────── */

    private async Task<AgentQueryResult?> RunClaudeQueryAsync(string apiKey, string prompt)
    {
        var schema = string.Join("\n", SchemaDoc.Select(kv => $"- {kv.Key}({kv.Value})"));
        var system = $$"""
            You are a SQL analyst for a healthcare RCM SQLite database. Convert the user's question
            into ONE read-only SQLite SELECT statement over ONLY these tables:
            {{schema}}

            Rules:
            - Output STRICT JSON: {"title": "...", "sql": "...", "summary": "..."} — nothing else.
            - Single SELECT only. No writes, no PRAGMA, no semicolons, no comments.
            - Always LIMIT to {{MaxRows}} rows or fewer.
            - Amount columns are decimals; TransactionDate/ServiceYear/ServiceMonth are text.
            - Join Facilities for readable facility names when grouping by FacilityId.
            - Prefer aggregated, report-style output (GROUP BY with totals) unless the user asks for raw rows.
            """;

        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var payload = new
        {
            model = "claude-sonnet-4-5",
            max_tokens = 700,
            system,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await client.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic API {Status}: {Body}", (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        var jsonMatch = Regex.Match(text, @"\{.*\}", RegexOptions.Singleline);
        if (!jsonMatch.Success) return null;

        using var plan = JsonDocument.Parse(jsonMatch.Value);
        var sql = plan.RootElement.TryGetProperty("sql", out var s) ? s.GetString() ?? "" : "";
        var title = plan.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Report" : "Report";
        var summary = plan.RootElement.TryGetProperty("summary", out var m) ? m.GetString() ?? "" : "";

        if (!IsSafeSelect(sql)) { _logger.LogWarning("Rejected unsafe agent SQL: {Sql}", sql); return null; }

        var (columns, rows) = await ExecuteSelectAsync(sql);
        return new AgentQueryResult(title, summary, columns, rows, sql, "claude");
    }

    private static bool IsSafeSelect(string sql)
    {
        sql = sql.Trim().TrimEnd(';');
        if (!sql.StartsWith("select", StringComparison.OrdinalIgnoreCase)) return false;
        if (ForbiddenSql.IsMatch(sql)) return false;
        // Only whitelisted table names may appear after FROM/JOIN
        foreach (Match m in Regex.Matches(sql, @"\b(?:from|join)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase))
        {
            if (!SchemaDoc.Keys.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /* ── Fallback path: deterministic report intents ────────────────── */

    private async Task<AgentQueryResult> RunBuiltInReportAsync(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        int? year = null;
        var ym = Regex.Match(p, @"\b(20\d{2})\b");
        if (ym.Success) year = int.Parse(ym.Groups[1].Value);

        string yearFilter(string col) => year == null ? "" : $" AND substr({col},1,4) = '{year}'";

        // Denials
        if (p.Contains("denial") || p.Contains("denied") || p.Contains("reject"))
        {
            if (p.Contains("payer"))
                return await Report(
                    "Denials by Payer",
                    $"""
                    SELECT COALESCE(NULLIF(rc.PayerCode,''),'Unknown') AS Payer,
                           COUNT(*) AS Claims,
                           SUM(CASE WHEN rc.ClaimCategory='FullyDenied' THEN 1 ELSE 0 END) AS FullyDenied,
                           SUM(CASE WHEN rc.ClaimCategory='PartiallyPaid' THEN 1 ELSE 0 END) AS PartiallyPaid,
                           ROUND(SUM(rc.OriginalAmount),2) AS BilledAmount,
                           ROUND(SUM(rc.OriginalAmount - rc.PaidAmount),2) AS DeniedAmount
                    FROM RemittanceClaims rc
                    WHERE rc.OriginalAmount > rc.PaidAmount
                    GROUP BY 1 ORDER BY DeniedAmount DESC LIMIT {MaxRows}
                    """,
                    "Denied and partially paid claims grouped by payer, sorted by denied amount.");

            return await Report(
                "Denials by Facility",
                $"""
                SELECT f.Name AS Facility,
                       COUNT(*) AS DeniedClaims,
                       ROUND(SUM(rc.OriginalAmount),2) AS BilledAmount,
                       ROUND(SUM(rc.OriginalAmount - rc.PaidAmount),2) AS DeniedAmount,
                       ROUND(100.0 * SUM(rc.OriginalAmount - rc.PaidAmount) / NULLIF(SUM(rc.OriginalAmount),0),1) AS DenialPct
                FROM RemittanceClaims rc JOIN Facilities f ON f.Id = rc.FacilityId
                WHERE rc.OriginalAmount > rc.PaidAmount
                GROUP BY f.Name ORDER BY DeniedAmount DESC LIMIT {MaxRows}
                """,
                "Claims with a denied balance grouped by facility.");
        }

        // Remittance / payments
        if (p.Contains("remittance") || p.Contains("paid") || p.Contains("payment") || p.Contains("collect"))
            return await Report(
                "Remittance Summary by Facility",
                $"""
                SELECT f.Name AS Facility,
                       COUNT(*) AS Claims,
                       ROUND(SUM(rc.OriginalAmount),2) AS BilledAmount,
                       ROUND(SUM(rc.PaidAmount),2) AS PaidAmount,
                       ROUND(100.0 * SUM(rc.PaidAmount) / NULLIF(SUM(rc.OriginalAmount),0),1) AS CollectionPct
                FROM RemittanceClaims rc JOIN Facilities f ON f.Id = rc.FacilityId
                GROUP BY f.Name ORDER BY BilledAmount DESC LIMIT {MaxRows}
                """,
                "Billed vs paid amounts per facility from parsed remittances.");

        // Claims by payer
        if (p.Contains("payer") || p.Contains("insurance"))
            return await Report(
                "Claims by Payer",
                $"""
                SELECT COALESCE(NULLIF(x.PayerName,''), NULLIF(x.PayerId,''), 'Unknown') AS Payer,
                       COUNT(*) AS Claims,
                       ROUND(SUM(x.NetAmount),2) AS NetAmount,
                       ROUND(SUM(x.PaidAmount),2) AS PaidAmount
                FROM XmlParsedRecords x
                WHERE x.RecordKind = 'Submission'{yearFilter("x.TransactionDate")}
                GROUP BY 1 ORDER BY Claims DESC LIMIT {MaxRows}
                """,
                "Submitted claims grouped by payer.");

        // Clinician productivity
        if (p.Contains("clinician") || p.Contains("doctor") || p.Contains("physician"))
            return await Report(
                "Claims by Clinician",
                $"""
                SELECT COALESCE(NULLIF(x.Clinician,''),'Unknown') AS Clinician,
                       COUNT(*) AS Claims,
                       ROUND(SUM(x.NetAmount),2) AS NetAmount,
                       SUM(x.ActivityCount) AS Activities
                FROM XmlParsedRecords x
                WHERE x.RecordKind = 'Submission'{yearFilter("x.TransactionDate")}
                GROUP BY 1 ORDER BY Claims DESC LIMIT {MaxRows}
                """,
                "Submitted claims grouped by treating clinician.");

        // Monthly trend
        if (p.Contains("month") || p.Contains("trend") || p.Contains("volume") || p.Contains("over time"))
            return await Report(
                "Monthly Transaction Volume",
                $"""
                SELECT COALESCE(NULLIF(t.SyncPeriod,''), substr(t.TransactionDate,1,7)) AS Month,
                       t.Portal,
                       COUNT(*) AS Transactions,
                       SUM(CASE WHEN t.FileDownloaded = 1 THEN 1 ELSE 0 END) AS FilesDownloaded
                FROM PortalTransactions t
                WHERE COALESCE(NULLIF(t.SyncPeriod,''), substr(t.TransactionDate,1,7)) IS NOT NULL{yearFilter("t.SyncPeriod")}
                GROUP BY 1, 2 ORDER BY 1 DESC, 2 LIMIT {MaxRows}
                """,
                "Portal transactions per month, split by portal.");

        // Files
        if (p.Contains("file") || p.Contains("download"))
            return await Report(
                "File Download Status by Facility",
                $"""
                SELECT f.Name AS Facility, t.Portal,
                       COUNT(*) AS Transactions,
                       SUM(CASE WHEN t.FileDownloaded = 1 THEN 1 ELSE 0 END) AS Downloaded,
                       SUM(CASE WHEN t.FileDownloaded = 0 THEN 1 ELSE 0 END) AS Pending
                FROM PortalTransactions t JOIN Facilities f ON f.Id = t.FacilityId
                GROUP BY f.Name, t.Portal ORDER BY Pending DESC LIMIT {MaxRows}
                """,
                "Downloaded vs pending transaction files per facility.");

        // Transaction types
        if (p.Contains("type") || p.Contains("transaction"))
            return await Report(
                "Transactions by Type",
                $"""
                SELECT t.Type, t.Portal, COUNT(*) AS Transactions
                FROM PortalTransactions t
                GROUP BY t.Type, t.Portal ORDER BY Transactions DESC LIMIT {MaxRows}
                """,
                "All synced portal transactions grouped by type and portal.");

        // Default: claims by facility
        return await Report(
            "Claims by Facility",
            $"""
            SELECT f.Name AS Facility,
                   COUNT(*) AS Claims,
                   ROUND(SUM(x.GrossAmount),2) AS GrossAmount,
                   ROUND(SUM(x.NetAmount),2) AS NetAmount,
                   SUM(x.ActivityCount) AS Activities
            FROM XmlParsedRecords x JOIN Facilities f ON f.Id = x.FacilityId
            WHERE x.RecordKind = 'Submission'{yearFilter("x.TransactionDate")}
            GROUP BY f.Name ORDER BY Claims DESC LIMIT {MaxRows}
            """,
            "Submitted claims grouped by facility. Try: denials by payer, remittance summary, monthly volume, claims by clinician.");
    }

    private async Task<AgentQueryResult> Report(string title, string sql, string summary)
    {
        var (columns, rows) = await ExecuteSelectAsync(sql);
        return new AgentQueryResult(title, summary, columns, rows, sql, "builtin");
    }

    /* ── Read-only SQL execution ─────────────────────────────────────── */

    private async Task<(List<string> columns, List<List<object?>> rows)> ExecuteSelectAsync(string sql)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.Trim().TrimEnd(';');
        cmd.CommandTimeout = 20;

        var columns = new List<string>();
        var rows = new List<List<object?>>();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        while (await reader.ReadAsync() && rows.Count < MaxRows)
        {
            var row = new List<object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row.Add(await reader.IsDBNullAsync(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return (columns, rows);
    }

    /* ── DTOs ────────────────────────────────────────────────────────── */

    public class AgentQueryRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";
    }

    public record AgentQueryResult(string Title, string Summary,
        List<string> Columns, List<List<object?>> Rows, string Sql, string Engine);
}
