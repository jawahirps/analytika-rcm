using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Analytika.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

public class AiAnalystResult
{
    public bool Success { get; set; }
    public string? Answer { get; set; }
    public string? Sql { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<List<string?>> Rows { get; set; } = new();
    public int RowCount { get; set; }
    public bool Redacted { get; set; }
    public int TotalTokens { get; set; }
    public string? Error { get; set; }
}

public interface INvidiaAnalystService
{
    Task<AiAnalystResult> AskAsync(string question, string? userId, string? userName, CancellationToken ct = default);
    /// <summary>
    /// Streaming variant: invokes <paramref name="emit"/> with progress events
    /// ({stage:…}), the executed SQL + result rows, then the answer token-by-token
    /// ({token:…}), and finally {done:true}. Powers the ChatGPT-style live response.
    /// </summary>
    Task AskStreamAsync(string question, string? userId, string? userName, Func<object, Task> emit, CancellationToken ct = default);
    /// <summary>Lightweight connectivity/auth probe used by the admin "Test connection" button.</summary>
    Task<(bool ok, string message)> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Data-analyst agent backed by an NVIDIA (OpenAI-compatible) chat model.
///
/// Safety model:
///  * Text-to-SQL only produces a SINGLE read-only SELECT. It is executed on a
///    dedicated read-only SQLite connection (Mode=ReadOnly + PRAGMA query_only).
///  * A table allow-list keeps the agent away from identity/secret tables
///    (AspNetUsers, PortalCredentials, SystemSettings, AiUsageLogs, sqlite_*).
///  * When AllowFullData is off, patient-identifier columns cannot be named in
///    SQL and are masked in any result rows before they are shown or sent to the
///    model — so PHI does not leave to the external API.
///  * Every request is capped (row limit) and metered (daily request / monthly
///    token limits) and written to AiUsageLogs for governance.
/// </summary>
public class NvidiaAnalystService : INvidiaAnalystService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiSettingsService _settingsService;
    private readonly AppDbContext _db;
    private readonly ILogger<NvidiaAnalystService> _log;

    // Identifiers that must never appear in generated SQL (sensitive tables).
    private static readonly string[] BlockedIdentifiers =
    {
        "AspNet", "PortalCredentials", "SystemSettings", "AiUsageLogs",
        "sqlite_", "dataprotection", "__EFMigrations"
    };

    // Block write/DDL STATEMENTS. Note: SQLite string functions REPLACE(...) are
    // legitimate in a SELECT, so only "REPLACE INTO" (the write form) is blocked;
    // likewise we don't blanket-ban words that double as functions.
    // "pragma\w*" also blocks the table-valued function form pragma_table_info(...)
    // — not just the PRAGMA statement.
    private static readonly Regex WriteKeyword = new(
        @"\b(insert|update|delete|drop|alter|create|attach|detach|pragma\w*|vacuum|reindex|truncate|grant|revoke)\b|\breplace\s+into\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches a SQLite single-quoted string literal (with '' escaping). String
    // literals are stripped before keyword/identifier scanning so that data values
    // like WHERE Status = 'update' or PayerName = 'AspNet Clinic' are not mistaken
    // for SQL keywords or blocked identifiers. (Values inside quotes can never act
    // as SQL, so removing them is safe and only removes false positives.)
    private static readonly Regex StringLiteral = new(
        @"'(?:[^']|'')*'", RegexOptions.Compiled);

    // Direct patient identifiers — blocked from SQL and masked in results when
    // AllowFullData is off. (Diagnosis columns are analytic and remain available.)
    private static readonly HashSet<string> PhiColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "PatientId", "MemberId", "PatientNationalId", "PatientDob",
        "PatientGender", "PatientName", "NationalId", "EmiratesId"
    };

    public NvidiaAnalystService(
        IHttpClientFactory httpFactory,
        IAiSettingsService settingsService,
        AppDbContext db,
        ILogger<NvidiaAnalystService> log)
    {
        _httpFactory = httpFactory;
        _settingsService = settingsService;
        _db = db;
        _log = log;
    }

    public async Task<AiAnalystResult> AskAsync(string question, string? userId, string? userName, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var settings = await _settingsService.GetAsync();
        var result = new AiAnalystResult { Redacted = !settings.AllowFullData };

        if (!settings.Enabled)
            return Fail(result, "The AI analyst is currently disabled. An administrator can enable it in Admin → AI Agent.");
        if (string.IsNullOrWhiteSpace(question))
            return Fail(result, "Please enter a question.");

        var apiKey = await _settingsService.GetApiKeyAsync();
        var openZenKey = await _settingsService.GetOpenZenApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey) && (!settings.OpenZenEnabled || string.IsNullOrWhiteSpace(openZenKey)))
            return Fail(result, "No primary or OpenZen fallback API key is configured. Add one in Admin → AI Agent.");

        // ---- usage limits ----
        var todayUtc = DateTime.UtcNow.Date;
        var monthStart = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var todayCount = await _db.AiUsageLogs.CountAsync(l => l.CreatedAt >= todayUtc, ct);
        if (settings.DailyRequestLimit > 0 && todayCount >= settings.DailyRequestLimit)
            return Fail(result, $"Daily request limit reached ({settings.DailyRequestLimit}). Try again tomorrow or raise the limit in Admin → AI Agent.");
        var monthTokens = await _db.AiUsageLogs.Where(l => l.CreatedAt >= monthStart).SumAsync(l => (long?)l.TotalTokens, ct) ?? 0;
        if (settings.MonthlyTokenLimit > 0 && monthTokens >= settings.MonthlyTokenLimit)
            return Fail(result, $"Monthly token budget reached ({settings.MonthlyTokenLimit:N0}). Raise it in Admin → AI Agent.");

        string? generatedSql = null;
        try
        {
            // ---- 1. text-to-SQL ----
            var sqlPrompt = BuildSqlSystemPrompt(settings.AllowFullData, settings.RowLimit);
            var (sqlRaw, t1) = await ChatAsync(settings, apiKey, sqlPrompt, question, ct);
            result.TotalTokens += t1;

            generatedSql = ExtractSql(sqlRaw);
            result.Sql = generatedSql;
            var (validSql, reason) = ValidateAndCap(generatedSql, settings.RowLimit, settings.AllowFullData);
            if (validSql == null)
            {
                await LogAsync(userId, userName, question, generatedSql, 0, result.TotalTokens, false, reason, settings.Model, sw, ct);
                return Fail(result, $"The generated query was rejected: {reason}");
            }

            // ---- 2. execute (read-only) ----
            var (cols, rows) = ExecuteReadOnly(validSql, settings.RowLimit, settings.AllowFullData, ct);
            result.Columns = cols;
            result.Rows = rows;
            result.RowCount = rows.Count;
            result.Sql = validSql;

            // ---- 3. summarise ----
            var table = RenderTable(cols, rows);
            var sumUser = $"Question: {question}\n\nQuery result ({rows.Count} row(s)):\n{table}";
            var (answer, t2) = await ChatAsync(settings, apiKey, SummarySystemPrompt, sumUser, ct);
            result.TotalTokens += t2;
            result.Answer = answer.Trim();
            result.Success = true;

            await LogAsync(userId, userName, question, validSql, rows.Count, result.TotalTokens, true, null, settings.Model, sw, ct);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI analyst request failed");
            await LogAsync(userId, userName, question, generatedSql, 0, result.TotalTokens, false, ex.Message, settings.Model, sw, ct);
            return Fail(result, ex.Message);
        }
    }

    // Warm, concise "colleague" voice — leads with the answer, cites the numbers,
    // adds one insight, offers a natural follow-up. Shared by streaming + non-streaming.
    private const string SummarySystemPrompt =
        "You are Bix's data analyst — sharp, friendly and concise, like a sharp colleague explaining numbers. " +
        "Answer the user's question using ONLY the query result provided; never invent figures that aren't in it. " +
        "Lead with the direct answer in one sentence, then the key numbers (thousands separators; prefix money with 'AED'). " +
        "Add one short insight when the data clearly shows one — a concentration, trend or outlier. " +
        "If the result is empty, say so plainly and suggest what to check (date range, facility, filter). " +
        "Keep it to a few short sentences; use a small markdown table only if it genuinely helps. " +
        "When natural, end with a brief follow-up suggestion. Do not mention SQL or that you queried a database.";

    public async Task AskStreamAsync(string question, string? userId, string? userName, Func<object, Task> emit, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var settings = await _settingsService.GetAsync();
        int totalTokens = 0;
        string? generatedSql = null;

        async Task Emit(object o) { try { await emit(o); } catch { } }

        try
        {
            if (!settings.Enabled) { await Emit(new { error = "The AI analyst is disabled. Enable it in Admin → AI Agent." }); return; }
            if (string.IsNullOrWhiteSpace(question)) { await Emit(new { error = "Please enter a question." }); return; }
            var apiKey = await _settingsService.GetApiKeyAsync();
            var openZenKey = await _settingsService.GetOpenZenApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey) && (!settings.OpenZenEnabled || string.IsNullOrWhiteSpace(openZenKey)))
            { await Emit(new { error = "No primary or OpenZen fallback API key is configured (Admin → AI Agent)." }); return; }

            // usage limits
            var todayUtc = DateTime.UtcNow.Date;
            var monthStart = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (settings.DailyRequestLimit > 0 && await _db.AiUsageLogs.CountAsync(l => l.CreatedAt >= todayUtc, ct) >= settings.DailyRequestLimit)
            { await Emit(new { error = $"Daily request limit reached ({settings.DailyRequestLimit})." }); return; }
            if (settings.MonthlyTokenLimit > 0 && (await _db.AiUsageLogs.Where(l => l.CreatedAt >= monthStart).SumAsync(l => (long?)l.TotalTokens, ct) ?? 0) >= settings.MonthlyTokenLimit)
            { await Emit(new { error = $"Monthly token budget reached ({settings.MonthlyTokenLimit:N0})." }); return; }

            // 1. text-to-SQL
            await Emit(new { stage = "understanding" });
            var (sqlRaw, t1) = await ChatAsync(settings, apiKey, BuildSqlSystemPrompt(settings.AllowFullData, settings.RowLimit), question, ct);
            totalTokens += t1;
            generatedSql = ExtractSql(sqlRaw);
            var (validSql, reason) = ValidateAndCap(generatedSql, settings.RowLimit, settings.AllowFullData);
            if (validSql == null)
            {
                await LogAsync(userId, userName, question, generatedSql, 0, totalTokens, false, reason, settings.Model, sw, ct);
                await Emit(new { error = $"I couldn't build a safe query for that: {reason}" });
                return;
            }

            // 2. execute (read-only)
            await Emit(new { stage = "querying" });
            var (cols, rows) = ExecuteReadOnly(validSql, settings.RowLimit, settings.AllowFullData, ct);
            await Emit(new { stage = "answering", sql = validSql, columns = cols, rows, rowCount = rows.Count, redacted = !settings.AllowFullData });

            // 3. summarise — streamed token by token
            var sumUser = $"Question: {question}\n\nQuery result ({rows.Count} row(s)):\n{RenderTable(cols, rows)}";
            var sb = new StringBuilder();
            var t2 = await ChatStreamAsync(settings, apiKey, SummarySystemPrompt, sumUser,
                async tok => { sb.Append(tok); await Emit(new { token = tok }); }, ct);
            totalTokens += t2;

            await LogAsync(userId, userName, question, validSql, rows.Count, totalTokens, true, null, settings.Model, sw, ct);
            await Emit(new { done = true, tokens = totalTokens });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI analyst stream failed");
            await LogAsync(userId, userName, question, generatedSql, 0, totalTokens, false, ex.Message, settings.Model, sw, ct);
            await Emit(new { error = ex.Message });
        }
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = await _settingsService.GetAsync();
        var apiKey = await _settingsService.GetApiKeyAsync();
        var fallbackKey = await _settingsService.GetOpenZenApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey) && (!settings.OpenZenEnabled || string.IsNullOrWhiteSpace(fallbackKey)))
            return (false, "No primary or OpenZen fallback API key configured.");
        try
        {
            var (reply, _) = await ChatAsync(settings, apiKey,
                "You are a connection test. Reply with the single word: OK.", "ping", ct);
            return (true, $"Connected. Model replied: \"{reply.Trim()}\".");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(string content, int tokens)> ChatAsync(
        AiSettings settings, string? apiKey, string system, string user, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try { return await SendChatAsync(settings.ApiBaseUrl, settings.Model, "NVIDIA", settings, apiKey, system, user, ct); }
            catch (Exception ex) when (settings.OpenZenEnabled && !ct.IsCancellationRequested)
            { _log.LogWarning(ex, "Primary AI provider failed; trying OpenZen fallback"); }
        }

        if (!settings.OpenZenEnabled)
            throw new InvalidOperationException("The primary AI provider is unavailable and OpenZen fallback is disabled.");
        var fallbackKey = await _settingsService.GetOpenZenApiKeyAsync();
        if (string.IsNullOrWhiteSpace(fallbackKey))
            throw new InvalidOperationException("The primary AI provider is unavailable and no OpenZen fallback key is configured.");
        return await SendChatAsync(settings.OpenZenApiBaseUrl, settings.OpenZenModel, "OpenZen", settings, fallbackKey, system, user, ct);
    }

    private async Task<(string content, int tokens)> SendChatAsync(
        string baseUrlValue, string model, string provider, AiSettings settings, string apiKey,
        string system, string user, CancellationToken ct)
    {
        var baseUrl = (baseUrlValue ?? "").TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = settings.Temperature,
            max_tokens = settings.MaxOutputTokens,
            stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var client = _httpFactory.CreateClient("nvidia");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 300)));

        using var resp = await client.SendAsync(req, timeoutCts.Token);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider} API returned {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var tt))
            tokens = tt.GetInt32();
        return (content, tokens);
    }

    // Streaming chat completion (OpenAI-compatible SSE). Invokes onToken for each
    // content delta as it arrives; returns total_tokens if the provider reports it.
    private async Task<int> ChatStreamAsync(
        AiSettings settings, string? apiKey, string system, string user, Func<string, Task> onToken, CancellationToken ct)
    {
        var emitted = false;
        async Task EmitTracked(string token) { emitted = true; await onToken(token); }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                return await SendChatStreamAsync(settings.ApiBaseUrl, settings.Model, "NVIDIA", settings,
                    apiKey, system, user, EmitTracked, ct);
            }
            catch (Exception ex) when (settings.OpenZenEnabled && !emitted && !ct.IsCancellationRequested)
            { _log.LogWarning(ex, "Primary AI stream failed before output; trying OpenZen fallback"); }
        }
        if (!settings.OpenZenEnabled)
            throw new InvalidOperationException("The primary AI provider is unavailable and OpenZen fallback is disabled.");
        var fallbackKey = await _settingsService.GetOpenZenApiKeyAsync();
        if (string.IsNullOrWhiteSpace(fallbackKey))
            throw new InvalidOperationException("The primary AI provider is unavailable and no OpenZen fallback key is configured.");
        return await SendChatStreamAsync(settings.OpenZenApiBaseUrl, settings.OpenZenModel, "OpenZen", settings,
            fallbackKey, system, user, onToken, ct);
    }

    private async Task<int> SendChatStreamAsync(
        string baseUrl, string model, string provider, AiSettings settings, string apiKey,
        string system, string user, Func<string, Task> onToken, CancellationToken ct)
    {
        var endpoint = $"{(baseUrl ?? "").TrimEnd('/')}/chat/completions";
        var payload = new
        {
            model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            temperature = settings.Temperature,
            max_tokens = settings.MaxOutputTokens,
            stream = true,
            stream_options = new { include_usage = true }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var client = _httpFactory.CreateClient("nvidia");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 300)));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"{provider} API returned {(int)resp.StatusCode}: {Truncate(err, 300)}");
        }

        var tokens = 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(timeoutCts.Token)) != null)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    && usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    tokens = tt.GetInt32();
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString();
                        if (!string.IsNullOrEmpty(piece)) await onToken(piece);
                    }
                }
            }
            catch (JsonException) { /* skip malformed keep-alive/comment lines */ }
        }
        return tokens;
    }

    private static string BuildSqlSystemPrompt(bool allowFullData, int rowLimit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a SQLite SQL generator for a healthcare revenue-cycle-management (RCM) database.");
        sb.AppendLine("Return ONLY a single read-only SELECT statement (a CTE via WITH is allowed). No prose, no markdown, no semicolons, no multiple statements.");
        sb.AppendLine($"Always include a LIMIT of at most {rowLimit}. Prefer aggregate queries (GROUP BY, SUM, COUNT, AVG) over row dumps.");
        if (!allowFullData)
            sb.AppendLine("PHI is restricted: do NOT select patient-identifier columns (PatientId, MemberId, PatientNationalId, PatientDob, PatientGender). Use aggregates instead.");
        sb.AppendLine("Only these tables exist:");
        sb.AppendLine(SchemaDoc(allowFullData));
        sb.AppendLine("Amounts are in AED.");
        sb.AppendLine(
            "DATE FORMAT (important): date columns (TransactionDate, SubmissionDate, TreatmentDate, " +
            "TreatmentDateEnd, DateOfAdmission, SettlementDate) are TEXT in 'dd/MM/yyyy HH:mm' format " +
            "(e.g. '31/12/2025 14:41') — they are NOT ISO. SQLite date functions (strftime, date, julianday) " +
            "and lexical/range comparisons will NOT work on them directly. To group or filter by year/month/day, " +
            "extract with substr: year = substr(col,7,4), month = substr(col,4,2), day = substr(col,1,2); " +
            "for a sortable key use substr(col,7,4)||'-'||substr(col,4,2). " +
            "Alternatively, for submission records use ServiceYear (TEXT year, e.g. '2024') and " +
            "ServiceMonth (full English month NAME, e.g. 'December' — not a number).");
        sb.AppendLine("Output the SQL and nothing else.");
        return sb.ToString();
    }

    private static string SchemaDoc(bool allowFullData)
    {
        var phi = allowFullData
            ? "PatientId, MemberId, PatientNationalId, PatientDob, PatientGender, "
            : "";
        return $@"
Facilities(Id, Name, IsActive)
XmlParsedRecords(Id, FacilityId->Facilities.Id, RecordKind, ClaimId, PayerId, PayerName, TransactionDate, TreatmentDate, SubmissionDate, EncounterType, Clinician, ServiceYear, ServiceMonth, GrossAmount, NetAmount, PaidAmount, ActivityCount, DenialCodesJson, PrincipalDiagnosis, DiagnosesJson, ClaimCategory, IsMatched, ReadyForReport, ParsedAt, {phi}Notes)
XmlParsedActivities(Id, XmlParsedRecordId->XmlParsedRecords.Id, ActivityCode, ActivityType, Quantity, Net, Gross, PaymentAmount, DenialCode, Clinician, OrderingClinician)
RemittanceClaims(Id, FacilityId->Facilities.Id, ClaimId, PayerClaimId, PayerCode, ClinicianLicense, OriginalAmount, PaidAmount, DenialCodesJson, ActivityCount, SettlementDate, PaymentReference, ParsedAt)
PortalTransactions(Id, FacilityId->Facilities.Id, Portal, TransactionId, FileId, FileName, FileDownloaded, SyncedAt)
ResubmissionTasks(Id, RemittanceClaimId->RemittanceClaims.Id, Status, Priority, DueDate, CreatedAt)
DhpoCodingSets(Id, Category, Code, Name)
Notes: RecordKind distinguishes submission vs remittance records. DenialCodesJson is a JSON array of denial codes.".Trim();
    }

    private static string ExtractSql(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        // Reasoning models (e.g. Nemotron "ultra") emit their chain-of-thought
        // first — strip <think>…</think> (and a dangling tag) before extracting.
        s = Regex.Replace(s, "<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, "</?think>", "", RegexOptions.IgnoreCase).Trim();
        // Prefer a fenced ```sql … ``` block; take the LAST one (final answer).
        var fences = Regex.Matches(s, "```(?:sql)?\\s*(.+?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fences.Count > 0)
            s = fences[^1].Groups[1].Value.Trim();
        else
        {
            // No fence: drop leading prose by starting at the first SELECT / WITH.
            var m = Regex.Match(s, @"(?is)\b(with|select)\b");
            if (m.Success) s = s[m.Index..].Trim();
        }
        // Single statement only: cut at the first semicolon (drops trailing prose).
        var semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi];
        return s.Trim();
    }

    private static (string? sql, string reason) ValidateAndCap(string sql, int rowLimit, bool allowFullData)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (null, "empty query.");
        var lower = sql.ToLowerInvariant();

        if (!(lower.StartsWith("select") || lower.StartsWith("with")))
            return (null, "only SELECT queries are allowed.");

        // Scan a copy with string literals removed, so data values (e.g.
        // WHERE Status = 'update') aren't mistaken for keywords/identifiers.
        var scan = StringLiteral.Replace(sql, "''");

        if (scan.Contains(';'))
            return (null, "multiple statements are not allowed.");
        if (WriteKeyword.IsMatch(scan))
            return (null, "the query contains a disallowed (write/DDL) keyword.");
        foreach (var bad in BlockedIdentifiers)
            if (scan.Contains(bad, StringComparison.OrdinalIgnoreCase))
                return (null, $"access to '{bad}' is not permitted.");
        if (!allowFullData)
            foreach (var col in PhiColumns)
                if (Regex.IsMatch(scan, $@"\b{Regex.Escape(col)}\b", RegexOptions.IgnoreCase))
                    return (null, $"patient-identifier column '{col}' is restricted. Ask for an aggregate instead.");

        // NOTE: no positive table allow-list here — it false-positived on CTEs,
        // subquery aliases, and any unlisted business table. Access to sensitive
        // tables is denied by BlockedIdentifiers above, and the query is executed
        // on a read-only connection (Mode=ReadOnly + PRAGMA query_only), so it can
        // only ever READ non-sensitive data regardless of which tables it names.

        // Ensure a LIMIT is present (defence-in-depth; execution also hard-caps).
        // Check the literal-stripped copy so a value like 'no limit' can't suppress it.
        if (!Regex.IsMatch(scan, @"\blimit\b", RegexOptions.IgnoreCase))
            sql += $" LIMIT {rowLimit}";

        return (sql, "");
    }

    private (List<string> cols, List<List<string?>> rows) ExecuteReadOnly(
        string sql, int rowLimit, bool allowFullData, CancellationToken ct)
    {
        var dbPath = GetDbPath();
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = 1;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        var cols = new List<string>();
        var rows = new List<List<string?>>();
        using var reader = cmd.ExecuteReader();
        for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));

        // Which result columns must be masked (SELECT * can surface PHI columns).
        var maskIdx = new HashSet<int>();
        if (!allowFullData)
            for (var i = 0; i < cols.Count; i++)
                if (PhiColumns.Contains(cols[i])) maskIdx.Add(i);

        while (reader.Read() && rows.Count < rowLimit)
        {
            ct.ThrowIfCancellationRequested();
            var row = new List<string?>(cols.Count);
            for (var i = 0; i < cols.Count; i++)
                row.Add(maskIdx.Contains(i) ? "***" : (reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()));
            rows.Add(row);
        }
        return (cols, rows);
    }

    private string GetDbPath()
    {
        var raw = _db.Database.GetDbConnection().ConnectionString;
        var b = new SqliteConnectionStringBuilder(raw);
        return b.DataSource;
    }

    private static string RenderTable(List<string> cols, List<List<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", cols));
        var max = Math.Min(rows.Count, 60); // keep the summariser prompt bounded
        for (var r = 0; r < max; r++)
            sb.AppendLine(string.Join(" | ", rows[r].Select(v => v ?? "")));
        if (rows.Count > max) sb.AppendLine($"... ({rows.Count - max} more rows)");
        return sb.ToString();
    }

    private async Task LogAsync(string? userId, string? userName, string question, string? sql,
        int rows, int tokens, bool success, string? error, string? model, Stopwatch sw, CancellationToken ct)
    {
        try
        {
            _db.AiUsageLogs.Add(new AiUsageLog
            {
                UserId = userId,
                UserName = userName,
                Question = Truncate(question, 2000),
                GeneratedSql = sql == null ? null : Truncate(sql, 4000),
                RowsReturned = rows,
                TotalTokens = tokens,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                Success = success,
                ErrorMessage = error == null ? null : Truncate(error, 1000),
                Model = model,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to write AiUsageLog"); }
    }

    private static AiAnalystResult Fail(AiAnalystResult r, string message)
    {
        r.Success = false;
        r.Error = message;
        return r;
    }

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);
}
