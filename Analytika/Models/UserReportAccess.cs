namespace Analytika.Models;

public class UserReportAccess
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty; // "Dashboard" or "Report"
    public string ResourceKey { get; set; } = string.Empty;  // tab name or report type
    public bool CanView { get; set; } = true;
    public ApplicationUser? User { get; set; }
}
