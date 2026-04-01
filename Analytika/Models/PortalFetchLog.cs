namespace Analytika.Models;

public class PortalFetchLog
{
    public int Id { get; set; }
    public string Portal { get; set; } = string.Empty; // "RHA" or "DHA"
    public int FacilityId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Success", "Failed"
    public string? ResponseSummary { get; set; }
    public int RecordsFetched { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public string FetchedBy { get; set; } = string.Empty;
    public Facility? Facility { get; set; }
}
