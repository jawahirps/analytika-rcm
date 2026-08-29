namespace Analytika.Models;

/// <summary>
/// One row per AI analyst request. Powers usage governance in the admin window
/// (request counts, token totals, cost visibility) and an audit trail of exactly
/// what was asked and which SQL the model produced.
/// </summary>
public class AiUsageLog
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? GeneratedSql { get; set; }
    public int RowsReturned { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int LatencyMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
