using System.Globalization;
using System.Text.Json;
using Analytika.Models.ViewModels;

namespace Analytika.Services;

/// <summary>
/// Riayati (RHA) TMB Post Office client — aligned to the official
/// "TMB API Integration Specifications v2.3":
///
///  * NO token endpoint exists. Auth = username + password sent as HTTP HEADERS on
///    every request (spec §5.1 "API Credentials"). 401 = bad credentials.
///  * Base URL: https://o-tmbapi.riayati.ae:8083 (note the "o-" host).
///  * Fetch = GET /api/Claim/Search and /api/Authorization/Search with querystring
///    filters (spec Appendix/Search): direction 0=received, 1=sent; downloaded
///    0=new, 1=downloaded, 2=all; dates in DD/MM/YYYY HH:MM. Max 500 rows/call —
///    callers already chunk by month.
///  * For a PROVIDER: sent claim traffic = submissions (direction=1), received
///    claim traffic = RemittanceAdvice (direction=0).
///  * Responses arrive in the TMB envelope { Entities:[...], StatusCode, Message,
///    Success, UserMessage, Error[] }.
/// </summary>
public class RhaPortalService : IRhaPortalService
{
    public const string DefaultBaseUrl = "https://o-tmbapi.riayati.ae:8083";
    private const string HeaderAuthToken = "header-auth"; // sentinel: auth is per-request headers, no bearer token exists

    private readonly IHttpClientFactory _httpClientFactory;

    public RhaPortalService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(string? token, string? error)> AuthenticateAsync(string username, string password, string baseUrl, string? apiKey = null)
    {
        // Spec has no token endpoint — validate the credentials with the cheapest
        // authenticated call (Claim/GetNew takes no parameters) and hand back a
        // sentinel so existing callers' token-flow keeps working unchanged.
        try
        {
            var client = CreateClient(username, password, apiKey);
            var response = await client.GetAsync($"{Base(baseUrl)}/api/Claim/GetNew");
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (null, $"Riayati rejected the credentials (401). Check username (facility license) and password (API key).{FormatErrorBody(body)}");
            if (!response.IsSuccessStatusCode)
                return (null, $"Auth check failed: {response.StatusCode}{FormatErrorBody(body)}");

            // Envelope-level auth errors can also ride in a 200
            if (TryParseEnvelope(body, out var env) && env.StatusCode == 401)
                return (null, $"Riayati rejected the credentials (envelope 401): {env.Message}");

            return (HeaderAuthToken, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public Task<(List<PortalFetchResultRow> rows, string? error)> GetClaimsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        // Provider's SENT claim traffic = ClaimSubmission / ClaimResubmission
        => SearchAsync(baseUrl, "/api/Claim/Search", direction: 1, fromDate, toDate, "Claim", token, apiKey);

    public Task<(List<PortalFetchResultRow> rows, string? error)> GetRemittancesAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        // Provider's RECEIVED claim traffic = RemittanceAdvice
        => SearchAsync(baseUrl, "/api/Claim/Search", direction: 0, fromDate, toDate, "Remittance", token, apiKey);

    public Task<(List<PortalFetchResultRow> rows, string? error)> GetPriorAuthorizationsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
        => SearchAsync(baseUrl, "/api/Authorization/Search", direction: 0, fromDate, toDate, "PriorAuth", token, apiKey);

    private async Task<(List<PortalFetchResultRow> rows, string? error)> SearchAsync(
        string baseUrl, string path, int direction, string? fromDate, string? toDate, string type, string credentialBlob, string? apiKey)
    {
        var rows = new List<PortalFetchResultRow>();
        try
        {
            // The "token" carried through callers is our sentinel; real auth headers
            // are re-derived from the credential each call by the controller giving us
            // username/password via CreateClient — but existing callers only pass the
            // sentinel here, so the client factory default headers set in
            // AuthenticateAsync's client don't persist. Instead the controller flow
            // calls AuthenticateAsync first (validating creds) and we stash them.
            var client = _lastClient ?? _httpClientFactory.CreateClient("RHA");

            var query = $"?direction={direction}&downloaded=2";
            var from = ToTmbDate(fromDate, endOfDay: false);
            var to = ToTmbDate(toDate, endOfDay: true);
            if (from != null) query += $"&fromDate={Uri.EscapeDataString(from)}";
            if (to != null) query += $"&toDate={Uri.EscapeDataString(to)}";

            var response = await client.GetAsync($"{Base(baseUrl)}{path}{query}");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (rows, $"HTTP {response.StatusCode}{FormatErrorBody(body)}");

            if (!TryParseEnvelope(body, out var env))
                return (rows, "Unrecognized TMB response shape" + FormatErrorBody(body));
            if (env.StatusCode is not (200 or 201))
                return (rows, $"TMB status {env.StatusCode}: {env.Message}");

            foreach (var item in env.Entities)
            {
                rows.Add(new PortalFetchResultRow
                {
                    FileId = GetStr(item, "ID", "id") ?? "-",
                    Type = type,
                    Status = GetStr(item, "Downloaded", "downloaded") is "True" or "true" ? "Downloaded" : "New",
                    Date = GetStr(item, "TransactionDate", "CreationDate", "transactionDate", "creationDate"),
                    Payer = GetStr(item, direction == 1 ? "ReceiverID" : "SenderID", "SenderID", "senderID"),
                    Amount = GetStr(item, "RecordCount", "recordCount"),
                    RawXml = item.GetRawText()
                });
            }
            return (rows, null);
        }
        catch (Exception ex) { return (rows, ex.Message); }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────

    private HttpClient? _lastClient;

    private HttpClient CreateClient(string username, string password, string? apiKey)
    {
        var client = _httpClientFactory.CreateClient("RHA");
        // Spec §5.1: credentials ride as plain request headers on every call.
        client.DefaultRequestHeaders.TryAddWithoutValidation("username", username);
        client.DefaultRequestHeaders.TryAddWithoutValidation("password", password);
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        _lastClient = client; // service is scoped per request; Search calls follow Authenticate
        return client;
    }

    private static string Base(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');

    /// <summary>yyyy-MM-dd (our UI) → DD/MM/YYYY HH:MM (TMB's only accepted format).</summary>
    private static string? ToTmbDate(string? value, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString(endOfDay ? "dd/MM/yyyy 23:59" : "dd/MM/yyyy 00:00", CultureInfo.InvariantCulture);
        return value; // already in portal format — pass through
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

    private static string FormatErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var trimmed = body.Trim();
        return trimmed.Length > 240 ? $": {trimmed[..240]}..." : $": {trimmed}";
    }
}
