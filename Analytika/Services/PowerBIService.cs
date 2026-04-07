using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Analytika.Services;

public class PowerBIService : IPowerBIService
{
    private readonly AppDbContext _context;
    private readonly ILogger<PowerBIService> _logger;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;

    private const string AadTokenCacheKey = "pbi_aad_token";

    public PowerBIService(
        AppDbContext context,
        ILogger<PowerBIService> logger,
        IConfiguration config,
        IHttpClientFactory httpFactory,
        IMemoryCache cache)
    {
        _context    = context;
        _logger     = logger;
        _config     = config;
        _httpFactory = httpFactory;
        _cache      = cache;
    }

    // ── Public API ────────────────────────────────────────────────

    public async Task<EmbedConfig?> GetEmbedConfigAsync(string tabName)
    {
        var embed = await _context.DashboardEmbeds
            .FirstOrDefaultAsync(e => e.TabName == tabName && e.IsActive);
        if (embed == null) return null;

        // Try to get a live embed token if credentials are properly configured
        if (IsConfigured() && HasRealIds(embed))
        {
            try
            {
                var aadToken = await GetAadTokenAsync();
                if (aadToken != null)
                {
                    var result = await GenerateEmbedTokenAsync(aadToken, embed.GroupId, embed.ReportId);
                    if (result.HasValue)
                    {
                        embed.EmbedToken  = result.Value.Token;
                        embed.TokenExpiry = result.Value.Expiry;
                        embed.EmbedUrl    = $"https://app.powerbi.com/reportEmbed?reportId={embed.ReportId}&groupId={embed.GroupId}";
                        await _context.SaveChangesAsync();

                        _logger.LogInformation("[PowerBI] Refreshed embed token for tab {tab}", tabName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PowerBI] Failed to get embed token for tab {tab}", tabName);
            }
        }

        return new EmbedConfig
        {
            AccessToken = embed.EmbedToken,
            EmbedUrl    = embed.EmbedUrl,
            ReportId    = embed.ReportId,
            TabName     = embed.TabName
        };
    }

    public async Task RefreshTokensAsync()
    {
        var expiring = await _context.DashboardEmbeds
            .Where(e => e.IsActive && e.TokenExpiry <= DateTime.UtcNow.AddMinutes(10))
            .ToListAsync();

        foreach (var embed in expiring)
            await GetEmbedConfigAsync(embed.TabName);
    }

    /// <summary>
    /// Verifies Azure AD credentials by fetching a token.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public async Task<string?> TestConnectionAsync()
    {
        if (!IsConfigured())
            return "Credentials not configured — fill in TenantId, ClientId and ClientSecret first.";
        try
        {
            var token = await GetAadTokenAsync(forceRefresh: true);
            return token == null ? "Authentication failed — check TenantId / ClientId / ClientSecret." : null;
        }
        catch (Exception ex)
        {
            return $"Connection error: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private bool IsConfigured()
    {
        var t = _config["PowerBI:TenantId"];
        var c = _config["PowerBI:ClientId"];
        var s = _config["PowerBI:ClientSecret"];
        return !string.IsNullOrWhiteSpace(t) && !t.StartsWith("YOUR_")
            && !string.IsNullOrWhiteSpace(c) && !c.StartsWith("YOUR_")
            && !string.IsNullOrWhiteSpace(s) && !s.StartsWith("YOUR_");
    }

    private static bool HasRealIds(DashboardEmbed embed) =>
        !string.IsNullOrWhiteSpace(embed.GroupId)
     && !string.IsNullOrWhiteSpace(embed.ReportId)
     && embed.GroupId.Length >= 32   // real GUIDs are 36 chars
     && !embed.ReportId.Contains("DEMO", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> GetAadTokenAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(AadTokenCacheKey, out string? cached))
            return cached;

        var tenantId     = _config["PowerBI:TenantId"]!;
        var clientId     = _config["PowerBI:ClientId"]!;
        var clientSecret = _config["PowerBI:ClientSecret"]!;

        var http = _httpFactory.CreateClient();
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type",    "client_credentials"),
            new KeyValuePair<string,string>("client_id",     clientId),
            new KeyValuePair<string,string>("client_secret", clientSecret),
            new KeyValuePair<string,string>("scope",         "https://analysis.windows.net/powerbi/api/.default")
        });

        var resp = await http.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", body);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("[PowerBI] AAD token request failed {status}: {err}", resp.StatusCode, err);
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var tokEl)) return null;
        var token = tokEl.GetString();

        var expiresIn = root.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;
        _cache.Set(AadTokenCacheKey, token, TimeSpan.FromSeconds(expiresIn - 60));

        return token;
    }

    private async Task<(string Token, DateTime Expiry)?> GenerateEmbedTokenAsync(
        string aadToken, string groupId, string reportId)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aadToken);

        var payload  = JsonSerializer.Serialize(new { accessLevel = "View" });
        var content  = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var endpoint = $"https://api.powerbi.com/v1.0/myorg/groups/{groupId}/reports/{reportId}/GenerateToken";

        var resp = await http.PostAsync(endpoint, content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("[PowerBI] GenerateToken failed {status}: {err}", resp.StatusCode, err);
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("token", out var tokEl)) return null;
        var expiry = root.TryGetProperty("expiration", out var expEl)
            ? DateTime.Parse(expEl.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : DateTime.UtcNow.AddHours(1);

        return (tokEl.GetString()!, expiry);
    }
}
