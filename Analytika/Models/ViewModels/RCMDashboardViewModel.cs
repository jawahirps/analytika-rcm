namespace Analytika.Models.ViewModels;

public class RCMDashboardViewModel
{
    public string ActiveTab { get; set; } = "Submissions";
    public string? EmbedToken { get; set; }
    public string? EmbedUrl { get; set; }
    public string? ReportId { get; set; }
    public List<string> Tabs { get; set; } = new() { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
}
