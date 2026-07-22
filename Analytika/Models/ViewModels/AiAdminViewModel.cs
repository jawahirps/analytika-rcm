using Analytika.Services;

namespace Analytika.Models.ViewModels;

public class AiAdminViewModel
{
    public AiSettings Settings { get; set; } = new();

    // Usage governance snapshot
    public int RequestsToday { get; set; }
    public long TokensThisMonth { get; set; }
    public int RequestsThisMonth { get; set; }
    public int TotalRequests { get; set; }
    public double SuccessRate { get; set; }   // 0..100

    public List<AiUsageLog> RecentLogs { get; set; } = new();
}
