namespace Analytika.Services;

public interface IPowerBIService
{
    Task<EmbedConfig?> GetEmbedConfigAsync(string tabName);
    Task RefreshTokensAsync();
    /// <summary>Returns null on success, or an error message string on failure.</summary>
    Task<string?> TestConnectionAsync();
}

public class EmbedConfig
{
    public string AccessToken { get; set; } = string.Empty;
    public string EmbedUrl { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string TabName { get; set; } = string.Empty;
}
