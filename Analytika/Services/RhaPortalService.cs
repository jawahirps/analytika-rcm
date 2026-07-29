using System.Globalization;
using System.Text.Json;
using Analytika.Models.ViewModels;

namespace Analytika.Services;

/// <summary>
/// Riayati (RHA) TMB Post Office client — implemented strictly to
/// "TMB API Integration Specifications v2.3".
///
/// Auth       : NO token endpoint. Exactly two headers on EVERY request —
///              `username` and `password` (Riayati-issued values).
/// Base URL   : https://o-tmbapi.riayati.ae:8083
/// Search     : GET /api/Claim/Search?license=&direction=&fromDate=&toDate=&downloaded=
///              direction 0=received (RemittanceAdvice for a provider), 1=sent
///              (ClaimSubmission/Resubmission); downloaded 0=new,1=downloaded,2=all;
///              dates DD/MM/YYYY HH:MM, boundaries EXCLUSIVE; max 500 rows per call.
/// View       : GET /api/Claim/View?id=&direction=   → full transaction content
/// GetNew     : GET /api/Claim/GetNew                → up to 500 unmarked transactions
/// SetDownloaded: POST /api/Claim/SetDownloaded?id=  → marks a transaction downloaded.
///              NOT called automatically: marking here also removes the transaction from
///              GetNew for every other system on the same account (e.g. the clinic HIS).
/// Envelope   : { Entities[] | Entity, StatusCode, Message, Success, UserMessage, Error[] }
/// </summary>
public class RhaPortalService : IRhaPortalService
{
    public const string DefaultBaseUrl = "https://o-tmbapi.riayati.ae:8083";
    private const string HeaderAuthToken = "header-auth";  // sentinel: no bearer token exists

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RhaPortalService> _logger;

    // Credentials for the current scope. AuthenticateAsync is always called first by the
    // sync flows; the service is registered scoped so these live exactly one request.
    private string? _username;
    private string? _password;

    public RhaPortalService(IHttpClientFactory httpClientFactory, ILogger<RhaPortalService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(string? token, string? error)> AuthenticateAsync(string username, string password, string baseUrl, string? apiKey = null)
    {
        _username = username;
        _password = password;
        try
        {
            // No login endpoint in the spec — validate the credentials with GetNew,
            // which takes no parameters and is the cheapest authenticated call.
            var client = CreateClient();
            var probeUrl = $"{Base(baseUrl)}/api/Claim/GetNew";

            // Diagnostic: exactly what goes on the wire (never the secret itself) so a
            // 401 can be attributed to transport/format vs the credential values.
            _logger.LogInformation(
                "[RHA] GET {Url} | headers sent: [{Headers}] | username='{User}' (len {UserLen}, trimmed-equal={UserClean}) | password len {PwdLen} (trimmed-equal={PwdClean}, uuid-shaped={PwdUuid})",
                probeUrl,
                string.Join(",", client.DefaultRequestHeaders.Select(h => h.Key)),
                username, username?.Length ?? 0, username == username?.Trim(),
                password?.Length ?? 0, password == password?.Trim(),
                password != null && System.Text.RegularExpressions.Regex.IsMatch(password, "^[0-9a-fA-F-]{36}$"));

            var response = await client.GetAsync(probeUrl);
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[RHA] response {Status}: {Body}", (int)response.StatusCode,
                body?.Length > 300 ? body[..300] : body);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (null, $"Riayati rejected the credentials (401). Username and password must be the Riayati-issued values.{Trim(body)}");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return (null, "Riayati rate limit (429) — wait a moment and retry.");
            if (!response.IsSuccessStatusCode)
                return (null, $"Auth check failed: {(int)response.StatusCode} {response.StatusCode}{Trim(body)}");

            if (TryParseEnvelope(body, out var env) && env.StatusCode == 401)
                return (null, $"Riayati rejected the credentials (envelope 401): {env.Message}");

            return (HeaderAuthToken, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // A provider SENDS claim submissions/resubmissions (direction=1) and RECEIVES
    // remittance advice (direction=0).
    public Task<(List<PortalFetchResultRow> rows, string? error)> GetClaimsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        => SearchAsync(baseUrl, direction: 1, fromDate, toDate, "Claim", apiKey);

    public Task<(List<PortalFetchResultRow> rows, string? error)> GetRemittancesAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        => SearchAsync(baseUrl, direction: 0, fromDate, toDate, "Remittance", apiKey);

    public Task<(List<PortalFetchResultRow> rows, string? error)> GetPriorAuthorizationsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        => SearchAsync(baseUrl, direction: 0, fromDate, toDate, "PriorAuth", apiKey, path: "/api/Authorization/Search", viewPath: "/api/Authorization/View");

    private async Task<(List<PortalFetchResultRow> rows, string? error)> SearchAsync(
        string baseUrl, int direction, string? fromDate, string? toDate, string type, string? license,
        string path = "/api/Claim/Search", string viewPath = "/api/Claim/View")
    {
        var rows = new List<PortalFetchResultRow>();
        try
        {
            var client = CreateClient();
            var url = $"{Base(baseUrl)}{path}?direction={direction}&downloaded=2";
            if (!string.IsNullOrWhiteSpace(license))
                url += $"&license={Uri.EscapeDataString(license!)}";
            // Boundaries are EXCLUSIVE — widen by a minute either side so a transaction
            // stamped exactly at 00:00 or 23:59 is not silently dropped.
            var from = ToTmbDate(fromDate, "00:00", -1);
            var to = ToTmbDate(toDate, "23:59", +1);
            if (from != null) url += $"&fromDate={Uri.EscapeDataString(from)}";
            if (to != null) url += $"&toDate={Uri.EscapeDataString(to)}";

            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (rows, $"HTTP {(int)response.StatusCode} {response.StatusCode}{Trim(body)}");
            if (!TryParseEnvelope(body, out var env))
                return (rows, "Unrecognized TMB response" + Trim(body));
            if (env.StatusCode is not (200 or 201))
                return (rows, $"TMB status {env.StatusCode}: {env.Message}");

            foreach (var item in env.Entities)
            {
                var id = GetStr(item, "ID", "id");
                rows.Add(new PortalFetchResultRow
                {
                    FileId = id ?? "-",
                    Type = type,
                    Status = GetStr(item, "Downloaded", "downloaded") is "True" or "true" ? "Downloaded" : "New",
                    Date = GetStr(item, "TransactionDate", "CreationDate"),
                    Payer = GetStr(item, direction == 1 ? "ReceiverID" : "SenderID", "SenderID"),
                    Amount = GetStr(item, "RecordCount"),
                    RawXml = item.GetRawText()
                });
            }

            // Search returns only transaction metadata. Pull the full content per row via
            // View so the parser has real claim/remittance data to work with (bounded
            // concurrency to stay polite to the portal).
            await EnrichWithViewAsync(rows, Base(baseUrl), viewPath, direction);
            return (rows, null);
        }
        catch (Exception ex) { return (rows, ex.Message); }
    }

    private async Task EnrichWithViewAsync(List<PortalFetchResultRow> rows, string baseUrl, string viewPath, int direction)
    {
        const int parallel = 4;
        foreach (var chunk in rows.Where(r => r.FileId != "-").Chunk(parallel))
        {
            await Task.WhenAll(chunk.Select(async row =>
            {
                try
                {
                    var client = CreateClient();
                    var resp = await client.GetAsync($"{baseUrl}{viewPath}?id={Uri.EscapeDataString(row.FileId!)}&direction={direction}");
                    if (!resp.IsSuccessStatusCode) return;
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    // View returns the transaction under "Entity" (object or array).
                    if (doc.RootElement.TryGetProperty("Entity", out var entity))
                        row.RawXml = entity.GetRawText();
                    else
                        row.RawXml = body;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "RHA View failed for transaction {Id}", row.FileId); }
            }));
        }
    }

    /// <summary>
    /// Marks a TMB transaction as downloaded. Deliberately NOT wired into the sync flow:
    /// the flag is account-wide, so marking here would remove the transaction from GetNew
    /// for the clinic's own HIS as well. Exposed for a future explicit, opt-in action.
    /// </summary>
    public async Task<(bool ok, string? error)> SetDownloadedAsync(string baseUrl, string transactionId)
    {
        try
        {
            var client = CreateClient();
            var resp = await client.PostAsync($"{Base(baseUrl)}/api/Claim/SetDownloaded?id={Uri.EscapeDataString(transactionId)}", null);
            var body = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)resp.StatusCode}{Trim(body)}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("RHA");
        // Spec §5.1 — EXACTLY these two headers, lowercase, on every request.
        client.DefaultRequestHeaders.TryAddWithoutValidation("username", _username ?? "");
        client.DefaultRequestHeaders.TryAddWithoutValidation("password", _password ?? "");
        return client;
    }

    private static string Base(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl!.TrimEnd('/');

    /// <summary>yyyy-MM-dd (UI) → DD/MM/YYYY HH:MM (the only format TMB accepts).</summary>
    private static string? ToTmbDate(string? value, string timePart, int dayShift)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.AddDays(dayShift == -1 ? -1 : 0).AddDays(dayShift == 1 ? 1 : 0)
                     .ToString($"dd/MM/yyyy {timePart}", CultureInfo.InvariantCulture);
        return value;
    }

    private readonly record struct TmbEnvelope(int StatusCode, string? Message, List<JsonElement> Entities);

    private static bool TryParseEnvelope(string body, out TmbEnvelope env)
    {
        env = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("StatusCode", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 200;
            var message = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
            var entities = new List<JsonElement>();
            if (root.TryGetProperty("Entities", out var e) && e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) entities.Add(item.Clone());
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var item in root.EnumerateArray()) entities.Add(item.Clone());
            env = new TmbEnvelope(status, message, entities);
            return true;
        }
        catch { return false; }
    }

    private static string? GetStr(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        return null;
    }

    private static string Trim(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var t = body.Trim();
        return t.Length > 240 ? $": {t[..240]}..." : $": {t}";
    }
}
