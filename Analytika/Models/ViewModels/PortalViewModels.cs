using Microsoft.AspNetCore.Mvc.Rendering;

namespace Analytika.Models.ViewModels;

public class PortalFetchViewModel
{
    public string Portal { get; set; } = "DHA"; // "DHA" or "RHA"
    public int? FacilityId { get; set; }
    public string Operation { get; set; } = "GetNewTransactions";
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? SearchText { get; set; }
    public int TransactionStatus { get; set; } = 1;  // 1=new, 2=downloaded (per DHPO spec)
    public int Direction { get; set; } = 2;           // 1=sent, 2=received (per DHPO spec)
    public int TransactionId { get; set; } = 2;       // 2=Claim, 8=Remittance, 16=PA.Request, 32=PA.Auth
    public int MinRecord { get; set; } = -1;           // -1=no filter (per DHPO spec)
    public int MaxRecord { get; set; } = -1;           // -1=no filter (per DHPO spec)
    public string? FileId { get; set; }
    public string? DownloadedFileName { get; set; }
    public string? DownloadedFileBase64 { get; set; }
    public List<SelectListItem> Facilities { get; set; } = new();
    public List<PortalFetchResultRow> Results { get; set; } = new();
    public List<PortalFetchLog> RecentLogs { get; set; } = new();
    public string? StatusMessage { get; set; }
    public bool IsError { get; set; }
    public int TotalFetched { get; set; }
}

public class PortalFetchResultRow
{
    public string FileId { get; set; } = string.Empty;   // FileID from <File FileID='...'/>
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? Date { get; set; }
    public string? Payer { get; set; }
    public string? Amount { get; set; }
    public string? RawXml { get; set; }
}

// ── Portal Sync (persist to DB) ─────────────────────────────────

public class PortalSyncViewModel
{
    public string Portal { get; set; } = "DHA";
    public int? FacilityId { get; set; }
    public string DateFrom { get; set; } = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd");
    public string DateTo { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public bool IncludeSearchTransactions { get; set; } = true;
    public bool IncludeNewTransactions { get; set; } = false;

    // Populated after run
    public List<SelectListItem> Facilities { get; set; } = new();
    public List<SyncBatchResult> BatchResults { get; set; } = new();
    public int TotalNew { get; set; }
    public int TotalDuplicates { get; set; }
    public int TotalErrors { get; set; }
    public int TotalFilesDownloaded { get; set; }
    public string? StatusMessage { get; set; }
    public bool IsError { get; set; }
    public bool HasRun { get; set; }

    // Existing DB counts for display
    public long TotalInDb { get; set; }
    public long TotalFilesInDb { get; set; }
}

public class SyncBatchResult
{
    public string Period { get; set; } = string.Empty;  // "2024-03"
    public string Label { get; set; } = string.Empty;   // "Mar 2024"
    public string Operation { get; set; } = string.Empty;
    public int Fetched { get; set; }
    public int NewRecords { get; set; }
    public int Duplicates { get; set; }
    public int FilesDownloaded { get; set; }
    public string? Error { get; set; }
    public bool IsSuccess => Error == null;
}

// ── Reconciliation ───────────────────────────────────────────────

public class ReconciliationViewModel
{
    public List<SelectListItem> Facilities { get; set; } = new();
    public int? FacilityId { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? StatusFilter { get; set; }   // "Paid" | "Partial" | "Rejected" | "Pending"
    public List<ReconciliationRow> Rows { get; set; } = new();

    // Summary (computed from capped Rows list)
    public int  TotalRowCount  { get; set; }          // full count before 500-row display cap
    public int  TotalClaims    => Rows.Count;
    public bool IsCapped       => TotalRowCount > Rows.Count;
    public decimal TotalSubmitted   => Rows.Sum(r => r.SubmittedAmount ?? 0);
    public decimal TotalPaid        => Rows.Sum(r => r.PaidAmount ?? 0);
    public decimal TotalOutstanding => TotalSubmitted - TotalPaid;
    public int PaidCount      => Rows.Count(r => r.PaymentStatus == "Paid");
    public int PartialCount   => Rows.Count(r => r.PaymentStatus == "Partial");
    public int RejectedCount  => Rows.Count(r => r.PaymentStatus == "Rejected");
    public int PendingCount   => Rows.Count(r => r.PaymentStatus == "Pending");
}

public class ReconciliationRow
{
    public string ClaimId { get; set; } = string.Empty;
    public string? Payer { get; set; }
    public string? ServiceDate { get; set; }
    public decimal? SubmittedAmount { get; set; }
    public string? RemittanceDate { get; set; }
    public decimal? PaidAmount { get; set; }
    public string PaymentStatus { get; set; } = "Pending"; // Paid | Partial | Rejected | Pending
    public decimal Difference => (SubmittedAmount ?? 0) - (PaidAmount ?? 0);
    public string? ClaimFileId { get; set; }
    public string? RemittanceFileId { get; set; }
    public int FacilityId { get; set; }
}

// ── Synced Transaction Browser ───────────────────────────────────

public class SyncedDataViewModel
{
    public List<SelectListItem> Facilities { get; set; } = new();
    public int? FacilityId { get; set; }
    public string? Portal { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? SearchText { get; set; }
    public List<PortalTransaction> Transactions { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}
