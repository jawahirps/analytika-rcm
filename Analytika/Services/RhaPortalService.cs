using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Analytika.Models.ViewModels;

namespace Analytika.Services;

public class RhaPortalService : IRhaPortalService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public RhaPortalService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(string? token, string? error)> AuthenticateAsync(string username, string password, string baseUrl, string? apiKey = null)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("RHA");
            AddApiKeyHeaders(client, apiKey);
            var payload = new Dictionary<string, string?>
            {
                ["username"] = username,
                ["userName"] = username,
                ["UserName"] = username,
                ["Username"] = username,
                ["password"] = password,
                ["Password"] = password,
                ["apiKey"] = apiKey,
                ["ApiKey"] = apiKey
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl.TrimEnd('/')}/auth/token", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return (null, $"Auth failed: {response.StatusCode}{FormatErrorBody(errorBody)}");
            }
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() :
                        doc.RootElement.TryGetProperty("token", out var t2) ? t2.GetString() : null;
            return (token, token == null ? "Token not found in response" : null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(List<PortalFetchResultRow> rows, string? error)> GetClaimsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
    {
        return await FetchResourceAsync(token, baseUrl, "/api/claims", fromDate, toDate, "Claim", apiKey);
    }

    public async Task<(List<PortalFetchResultRow> rows, string? error)> GetRemittancesAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
    {
        return await FetchResourceAsync(token, baseUrl, "/api/remittances", fromDate, toDate, "Remittance", apiKey);
    }

    public async Task<(List<PortalFetchResultRow> rows, string? error)> GetPriorAuthorizationsAsync(string token, string baseUrl, string? fromDate, string? toDate, string? apiKey = null)
    {
        return await FetchResourceAsync(token, baseUrl, "/api/priorauthorizations", fromDate, toDate, "PriorAuth", apiKey);
    }

    private async Task<(List<PortalFetchResultRow> rows, string? error)> FetchResourceAsync(
        string token, string baseUrl, string path, string? fromDate, string? toDate, string type, string? apiKey = null)
    {
        var rows = new List<PortalFetchResultRow>();
        try
        {
            var client = _httpClientFactory.CreateClient("RHA");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddApiKeyHeaders(client, apiKey);
            var query = $"?fromDate={fromDate}&toDate={toDate}";
            var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}{path}{query}");
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return (rows, $"HTTP {response.StatusCode}{FormatErrorBody(errorBody)}");
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement :
                      doc.RootElement.TryGetProperty("data", out var d) ? d :
                      doc.RootElement.TryGetProperty("result", out var r) ? r : default;

            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    rows.Add(new PortalFetchResultRow
                    {
                        FileId = GetStr(item, "id", "claimId", "transactionId") ?? "-",
                        Type = type,
                        Status = GetStr(item, "status", "claimStatus") ?? "-",
                        Date = GetStr(item, "date", "submissionDate", "claimDate"),
                        Payer = GetStr(item, "payer", "payerName", "insurerName"),
                        Amount = GetStr(item, "amount", "grossAmount", "totalAmount"),
                        RawXml = item.GetRawText()
                    });
                }
            }
            return (rows, null);
        }
        catch (Exception ex) { return (rows, ex.Message); }
    }

    private static string? GetStr(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v)) return v.GetString();
        return null;
    }

    private static void AddApiKeyHeaders(HttpClient client, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", apiKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("ApiKey", apiKey);
    }

    private static string FormatErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var trimmed = body.Trim();
        return trimmed.Length > 240 ? $": {trimmed[..240]}..." : $": {trimmed}";
    }
}
