namespace Analytika.Models;

public class DashboardEmbed
{
    public int Id { get; set; }
    public string TabName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string EmbedToken { get; set; } = string.Empty;
    public string EmbedUrl { get; set; } = string.Empty;
    public DateTime TokenExpiry { get; set; }
    public bool IsActive { get; set; } = true;
}
