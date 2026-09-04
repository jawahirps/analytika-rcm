using Analytika.Models;
using Analytika.Security;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Strongly-typed view of the AI analyst configuration. The API key is never
/// carried on this object — only <see cref="HasApiKey"/> tells the UI whether one
/// is stored. The plaintext key is fetched separately, server-side, via
/// <see cref="IAiSettingsService.GetApiKeyAsync"/>.
/// </summary>
public class AiSettings
{
    public bool Enabled { get; set; }
    public string ApiBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
    public string Model { get; set; } = "nvidia/nemotron-3-ultra-550b-a55b";
    public bool HasApiKey { get; set; }
    public int MaxOutputTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.2;
    public int RowLimit { get; set; } = 200;
    public int DailyRequestLimit { get; set; } = 200;
    public long MonthlyTokenLimit { get; set; } = 2_000_000;
    /// <summary>false = aggregate + PHI-redacted (default, safest); true = row-level patient data may reach the model.</summary>
    public bool AllowFullData { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public bool OpenZenEnabled { get; set; }
    public string OpenZenApiBaseUrl { get; set; } = "https://opencode.ai/zen/v1";
    public string OpenZenModel { get; set; } = "gpt-5.2";
    public bool HasOpenZenApiKey { get; set; }
}

public interface IAiSettingsService
{
    Task<AiSettings> GetAsync();
    Task<string?> GetApiKeyAsync();
    Task<string?> GetOpenZenApiKeyAsync();
    /// <summary>Persists settings. When <paramref name="newApiKey"/> is null/blank the stored key is left unchanged.</summary>
    Task SaveAsync(AiSettings settings, string? newApiKey, string? newOpenZenApiKey = null);
}

public class AiSettingsService : IAiSettingsService
{
    private const string Cat = "AI";
    private readonly AppDbContext _db;
    private readonly ICredentialProtector _protector;

    public AiSettingsService(AppDbContext db, ICredentialProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<AiSettings> GetAsync()
    {
        var map = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Category == Cat)
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var s = new AiSettings();
        if (map.TryGetValue("Enabled", out var en)) s.Enabled = ParseBool(en);
        if (map.TryGetValue("ApiBaseUrl", out var url) && !string.IsNullOrWhiteSpace(url)) s.ApiBaseUrl = url!.Trim();
        if (map.TryGetValue("Model", out var m) && !string.IsNullOrWhiteSpace(m)) s.Model = m!.Trim();
        if (map.TryGetValue("MaxOutputTokens", out var mo) && int.TryParse(mo, out var moi)) s.MaxOutputTokens = moi;
        if (map.TryGetValue("Temperature", out var tp) && double.TryParse(tp,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tpd))
            s.Temperature = tpd;
        if (map.TryGetValue("RowLimit", out var rl) && int.TryParse(rl, out var rli)) s.RowLimit = rli;
        if (map.TryGetValue("DailyRequestLimit", out var dr) && int.TryParse(dr, out var dri)) s.DailyRequestLimit = dri;
        if (map.TryGetValue("MonthlyTokenLimit", out var mt) && long.TryParse(mt, out var mtl)) s.MonthlyTokenLimit = mtl;
        if (map.TryGetValue("AllowFullData", out var af)) s.AllowFullData = ParseBool(af);
        if (map.TryGetValue("TimeoutSeconds", out var ts) && int.TryParse(ts, out var tsi)) s.TimeoutSeconds = tsi;
        if (map.TryGetValue("OpenZenEnabled", out var oze)) s.OpenZenEnabled = ParseBool(oze);
        if (map.TryGetValue("OpenZenApiBaseUrl", out var ozu) && !string.IsNullOrWhiteSpace(ozu)) s.OpenZenApiBaseUrl = ozu!.Trim();
        if (map.TryGetValue("OpenZenModel", out var ozm) && !string.IsNullOrWhiteSpace(ozm)) s.OpenZenModel = ozm!.Trim();
        s.HasApiKey = map.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key);
        s.HasOpenZenApiKey = map.TryGetValue("OpenZenApiKey", out var ozk) && !string.IsNullOrWhiteSpace(ozk);
        return s;
    }

    public async Task<string?> GetApiKeyAsync()
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Category == Cat && s.Key == "ApiKey");
        if (row?.Value is not { Length: > 0 } stored) return null;
        try { return _protector.Unprotect(stored); }
        catch { return null; }
    }

    public async Task<string?> GetOpenZenApiKeyAsync()
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Category == Cat && s.Key == "OpenZenApiKey");
        if (row?.Value is not { Length: > 0 } stored) return null;
        try { return _protector.Unprotect(stored); }
        catch { return null; }
    }

    public async Task SaveAsync(AiSettings settings, string? newApiKey, string? newOpenZenApiKey = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Enabled"] = settings.Enabled.ToString(),
            ["ApiBaseUrl"] = settings.ApiBaseUrl?.Trim(),
            ["Model"] = settings.Model?.Trim(),
            ["MaxOutputTokens"] = settings.MaxOutputTokens.ToString(),
            ["Temperature"] = settings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RowLimit"] = settings.RowLimit.ToString(),
            ["DailyRequestLimit"] = settings.DailyRequestLimit.ToString(),
            ["MonthlyTokenLimit"] = settings.MonthlyTokenLimit.ToString(),
            ["AllowFullData"] = settings.AllowFullData.ToString(),
            ["TimeoutSeconds"] = settings.TimeoutSeconds.ToString(),
            ["OpenZenEnabled"] = settings.OpenZenEnabled.ToString(),
            ["OpenZenApiBaseUrl"] = settings.OpenZenApiBaseUrl?.Trim(),
            ["OpenZenModel"] = settings.OpenZenModel?.Trim(),
        };

        // Only overwrite the key when a new one is supplied; blank means "keep current".
        if (!string.IsNullOrWhiteSpace(newApiKey))
            values["ApiKey"] = _protector.Protect(newApiKey.Trim());
        if (!string.IsNullOrWhiteSpace(newOpenZenApiKey))
            values["OpenZenApiKey"] = _protector.Protect(newOpenZenApiKey.Trim());

        foreach (var kv in values)
        {
            if (kv.Value == null) continue;
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Category == Cat && s.Key == kv.Key);
            if (row == null)
                _db.SystemSettings.Add(new SystemSetting { Category = Cat, Key = kv.Key, Value = kv.Value });
            else { row.Value = kv.Value; row.UpdatedAt = DateTime.UtcNow; }
        }
        await _db.SaveChangesAsync();
    }

    private static bool ParseBool(string? v) =>
        bool.TryParse(v, out var b) ? b : v == "1" || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
}
