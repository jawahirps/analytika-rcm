namespace Analytika.Models.ViewModels;

public class RCMDashboardViewModel
{
    public string ActiveTab { get; set; } = "Submissions";
    public string? EmbedToken { get; set; }
    public string? EmbedUrl { get; set; }
    public string? ReportId { get; set; }
    public List<string> Tabs { get; set; } = new() { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
}

// ── Facility Status Dashboard ─────────────────────────────────────

public enum FacilityConnectionStatus { Connected, Degraded, Disconnected }

public class FacilityStatusViewModel
{
    public List<FacilityStatusRow> Facilities { get; set; } = new();
    public int ConnectedCount     => Facilities.Count(f => f.Status == FacilityConnectionStatus.Connected);
    public int DegradedCount      => Facilities.Count(f => f.Status == FacilityConnectionStatus.Degraded);
    public int DisconnectedCount  => Facilities.Count(f => f.Status == FacilityConnectionStatus.Disconnected);
    public int TotalRecords       { get; set; }
    public int TotalFiles         { get; set; }
    public string? LastSyncTime   { get; set; }
}

public class FacilityStatusRow
{
    public int    FacilityId     { get; set; }
    public string FacilityName   { get; set; } = "";
    public bool   HasCredential  { get; set; }   // any active credential
    public string? Portal        { get; set; }   // DHA / RHA / both
    public string? LastSyncTime  { get; set; }
    public string? LastSyncStatus{ get; set; }   // Success / Error / null
    public int    RecordCount    { get; set; }
    public int    FileCount      { get; set; }

    public FacilityConnectionStatus Status
    {
        get
        {
            if (!HasCredential) return FacilityConnectionStatus.Disconnected;
            if (LastSyncStatus == "Success") return FacilityConnectionStatus.Connected;
            if (LastSyncTime != null) return FacilityConnectionStatus.Degraded; // has cred + old/failed sync
            return FacilityConnectionStatus.Disconnected;
        }
    }
}
