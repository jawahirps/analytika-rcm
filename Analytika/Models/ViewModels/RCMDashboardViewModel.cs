namespace Analytika.Models.ViewModels;

public class RCMDashboardViewModel
{
    public string ActiveTab { get; set; } = "Submissions";
    public List<string> Tabs { get; set; } = new() { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
    public string StableFieldTitle { get; set; } = "Encounter Date";
    public string StableFieldDetail { get; set; } = "Shared submission anchor used across dashboard views.";
    public List<DashboardMetric> Metrics { get; set; } = new();
    public List<DashboardTrendPoint> Trend { get; set; } = new();
    public List<DashboardBreakdownItem> Breakdown { get; set; } = new();
    public List<DashboardInsight> Insights { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public DateTime RefreshedAt { get; set; } = DateTime.Now;
}

public class DashboardMetric
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Delta { get; set; } = string.Empty;
    public string Icon { get; set; } = "fa-chart-line";
    public string Tone { get; set; } = "teal";
}

public class DashboardTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class DashboardBreakdownItem
{
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public class DashboardInsight
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Status { get; set; } = "Stable";
}

// ── Facility Status Dashboard ─────────────────────────────────────

public enum FacilityConnectionStatus { Connected, Degraded, Disconnected }

public class FacilityStatusViewModel
{
    public List<FacilityStatusRow> Facilities { get; set; } = new();
    public int ConnectedCount => Facilities.Count(f => f.Status == FacilityConnectionStatus.Connected);
    public int DegradedCount => Facilities.Count(f => f.Status == FacilityConnectionStatus.Degraded);
    public int DisconnectedCount => Facilities.Count(f => f.Status == FacilityConnectionStatus.Disconnected);
    public int TotalRecords { get; set; }
    public int TotalClaimCount { get; set; }
    public int TotalFiles { get; set; }
    public string? LastSyncTime { get; set; }
}

public class FacilityStatusRow
{
    public int FacilityId { get; set; }
    public string FacilityName { get; set; } = "";
    public bool HasCredential { get; set; }   // any active credential
    public string? Portal { get; set; }   // DHA / RHA / both
    public string? LastSyncTime { get; set; }
    public string? LastSyncStatus { get; set; }   // Success / Error / null
    public int RecordCount { get; set; }
    public int ClaimCount { get; set; }
    public int FileCount { get; set; }
    public int DownloadedFilesCount { get; set; }  // files where FileDownloaded = true
    public int PendingFilesCount { get; set; }  // files where FileDownloaded = false
    public int TotalFilesWithStatus => DownloadedFilesCount + PendingFilesCount;

    public FacilityConnectionStatus Status
    {
        get
        {
            if (!HasCredential) return FacilityConnectionStatus.Disconnected;
            if (LastSyncStatus == "Success") return FacilityConnectionStatus.Connected;
            if (LastSyncTime != null) return FacilityConnectionStatus.Degraded;
            return FacilityConnectionStatus.Disconnected;
        }
    }

    public string StatusReason => Status switch
    {
        FacilityConnectionStatus.Connected => $"Last sync: {LastSyncTime}",
        FacilityConnectionStatus.Degraded => $"Last meaningful sync did not succeed — {LastSyncTime}",
        FacilityConnectionStatus.Disconnected => HasCredential ? "Credential exists but never synced" : "No active credential",
        _ => ""
    };
}
