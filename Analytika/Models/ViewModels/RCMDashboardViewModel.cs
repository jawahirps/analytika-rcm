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
    public List<RcmLifecycleStage> Lifecycle { get; set; } = new();
    public int UnmatchedRecords { get; set; }
    public decimal UnmatchedAmount { get; set; }
    public double ReconciliationRate { get; set; }
    public List<RcmUnmatchedRow> UnmatchedWorklist { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public DateTime RefreshedAt { get; set; } = DateTime.Now;
    public RcmDashboardFilters Filters { get; set; } = new();
    public List<DashboardFilterOption> FacilityOptions { get; set; } = new();
    public List<DashboardFilterOption> ReceiverOptions { get; set; } = new();
    public List<DashboardFilterOption> PayerOptions { get; set; } = new();
    public List<DashboardFilterOption> EncounterTypeOptions { get; set; } = new();
    public bool HasActiveFilters =>
        Filters.FacilityIds.Count > 0 ||
        Filters.Receivers.Count > 0 ||
        Filters.Payers.Count > 0 ||
        Filters.EncounterTypes.Count > 0 ||
        Filters.DateFrom.HasValue ||
        Filters.DateTo.HasValue;
}

public class RcmUnmatchedRow
{
    public string RecordKind { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ServiceDate { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Issue { get; set; } = string.Empty;
}

public class RcmLifecycleStage
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public string Icon { get; set; } = "fa-circle";
    public string Tone { get; set; } = "teal";
}

public class RcmDashboardFilters
{
    public List<int> FacilityIds { get; set; } = new();
    public List<string> Receivers { get; set; } = new();
    public List<string> Payers { get; set; } = new();
    public List<string> EncounterTypes { get; set; } = new();
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
}

public class DashboardFilterOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
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

    /// <summary>
    /// True when this model is a placeholder returned while the aggregation is still
    /// running (cold cache after a restart), rather than a finished result. Without it
    /// the view cannot tell "still loading" from "genuinely no facilities" and showed
    /// "No active facilities found. Add credentials to get started." to users who had
    /// 12 working facilities — a false statement prescribing a wrong action.
    /// </summary>
    public bool IsBuilding { get; set; }
}

public class FacilityStatusRow
{
    public int FacilityId { get; set; }
    public string FacilityName { get; set; } = "";
    public string? FullName { get; set; }        // official DHPO license name
    public string? LicenseCode { get; set; }     // DHA-F-xxxxx
    public bool HasCredential { get; set; }   // any active credential
    public string? Portal { get; set; }   // DHA / RHA / both
    public string? LastSyncTime { get; set; }
    public string? LastSyncStatus { get; set; }   // Success / Error / null
    public int RecordCount { get; set; }
    public int ClaimCount { get; set; }
    public int FileCount { get; set; }
    public int DownloadedFilesCount { get; set; }  // files where FileDownloaded = true
    public int PendingFilesCount { get; set; }  // files where FileDownloaded = false
    public int ParsedFilesCount { get; set; }  // transactions parsed into XmlParsedRecords (report-ready)
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
