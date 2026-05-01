namespace Analytika.Models;

/// <summary>
/// Key-value store for configurable system settings (SMTP, integrations, etc.)
/// Values here override appsettings.json at runtime.
/// </summary>
public class SystemSetting
{
    public int Id { get; set; }
    public string Category { get; set; } = string.Empty;   // e.g. "SMTP", "PowerBI"
    public string Key { get; set; } = string.Empty;   // e.g. "Host", "Port"
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
