using Microsoft.AspNetCore.Mvc.Rendering;

namespace Analytika.Models.ViewModels;

public class PortalFetchViewModel
{
    public string Portal { get; set; } = "DHA"; // "DHA" or "RHA"
    public List<int> FacilityIds { get; set; } = new();              // multi-select
    public int? FacilityId => FacilityIds.Count > 0 ? FacilityIds[0] : null;
    public string Operation { get; set; } = "GetNewTransactions";
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? SearchText { get; set; }
    // multi-select lists (replace old single-value fields)
    public List<int> TransactionStatuses { get; set; } = new() { 1 };       // 1=new, 2=downloaded
    public List<int> Directions { get; set; } = new() { 2 };       // 1=sent, 2=received
    public List<int> TransactionIds { get; set; } = new() { 2 };       // 2=Claim, 8=Remittance, 16=PA.Req, 32=PA.Auth
    // single-value compat (first selected or default)
    public int TransactionStatus => TransactionStatuses.Count > 0 ? TransactionStatuses[0] : 1;
    public int Direction => Directions.Count > 0 ? Directions[0] : 2;
    public int TransactionId => TransactionIds.Count > 0 ? TransactionIds[0] : 2;
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
    public int FacilityId { get; set; }
    public string? FacilityName { get; set; }
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

// ── XML Parsing Summary Dashboard ────────────────────────────────

public class XmlParsingViewModel
{
    public List<SelectListItem> Facilities { get; set; } = new();
    public List<int> FacilityIds { get; set; } = new();
    public string? SearchText { get; set; }
    public string Kind { get; set; } = "All";
    public List<XmlParsingFacilityRow> FacilityRows { get; set; } = new();
    public List<XmlParsingRecordRow> Records { get; set; } = new();
    public List<XmlParsingParsedRecordRow> ParsedRecords { get; set; } = new();
    public int ParsedRecordTotal { get; set; }

    // Grand totals
    public int TotalSubmission => FacilityRows.Sum(r => r.SubmissionTotal);
    public int TotalSubmissionDownloaded => FacilityRows.Sum(r => r.SubmissionDownloaded);
    public int TotalRemittance => FacilityRows.Sum(r => r.RemittanceTotal);
    public int TotalRemittanceDownloaded => FacilityRows.Sum(r => r.RemittanceDownloaded);
    public int TotalRaRecords => FacilityRows.Sum(r => r.RemittanceRecordCount);
    public int TotalRaClaimRefs => FacilityRows.Sum(r => r.RemittanceClaimRefCount);
    public int TotalSubmissionRecords => FacilityRows.Sum(r => r.SubmissionRecordCount);
    public int TotalMatched => FacilityRows.Sum(r => r.Matched);
    public int TotalUnmatched => FacilityRows.Sum(r => r.UnmatchedSubmissions);
    public int TotalUnmatchedRemittances => FacilityRows.Sum(r => r.UnmatchedRemittances);
    public int TotalClaimCount => FacilityRows.Sum(r => r.ClaimCount);
    public int TotalParsedRows => Records.Sum(r => r.ParsedRows);
    public int TotalReadyRows => Records.Sum(r => r.ReadyRows);
    public int TotalRecordRows => Records.Count;
}

public class XmlParsingFacilityRow
{
    public int FacilityId { get; set; }
    public string FacilityName { get; set; } = "";
    // Submission (Claim) files
    public int SubmissionTotal { get; set; }
    public int SubmissionDownloaded { get; set; }
    // Remittance files
    public int RemittanceTotal { get; set; }
    public int RemittanceDownloaded { get; set; }
    public int RemittanceRecordCount { get; set; }  // parsed RA claim rows from downloaded RA XML
    public int RemittanceClaimRefCount { get; set; }  // distinct RA claim refs
    public int SubmissionRecordCount { get; set; }  // parsed submission claim rows
    // Matching
    public int Matched { get; set; }
    public int UnmatchedSubmissions { get; set; }  // claims with no remittance
    public int UnmatchedRemittances { get; set; }  // remittances with no claim
    public int ClaimCount { get; set; }  // total <Claim> elements across all submission XMLs
    public decimal MatchRate => ClaimCount > 0
        ? Math.Round((decimal)Matched / ClaimCount * 100, 1) : 0;
}

public class XmlParsingRecordRow
{
    public int TransactionDbId { get; set; }
    public int FacilityId { get; set; }
    public string FacilityName { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Direction { get; set; }
    public string Status { get; set; } = "";
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? TransactionDate { get; set; }
    public string? Payer { get; set; }
    public string? Amount { get; set; }
    public bool FileDownloaded { get; set; }
    public bool HasXml { get; set; }
    public int ParsedRows { get; set; }
    public int ReadyRows { get; set; }
    public int SubmissionRows { get; set; }
    public int RemittanceRows { get; set; }
    public int MatchedRows { get; set; }
    public string SampleClaimId { get; set; } = "";
    public DateTime SyncedAt { get; set; }
    public DateTime? ParsedAt { get; set; }
    public bool IsReady => ReadyRows > 0;
    public bool IsMatched => MatchedRows > 0;
}

public class XmlParsingParsedRecordRow
{
    public int Id { get; set; }
    public int PortalTransactionId { get; set; }
    public int FacilityId { get; set; }
    public string FacilityName { get; set; } = "";
    public string RecordKind { get; set; } = "";
    public string ClaimId { get; set; } = "";
    public string? FileName { get; set; }
    public string? FileId { get; set; }
    public string? TransactionDate { get; set; }
    public string? SenderId { get; set; }
    public string? ReceiverId { get; set; }
    public string? PayerName { get; set; }
    public decimal NetAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string? SettlementDate { get; set; }
    public string? PaymentReference { get; set; }
    public string? DenialCodes { get; set; }
    public string? Comments { get; set; }
    public bool IsMatched { get; set; }
    public bool ReadyForReport { get; set; }
    public DateTime ParsedAt { get; set; }
}

// ── Reconciliation (kept for backward compat) ─────────────────────

public class ReconciliationViewModel
{
    public List<SelectListItem> Facilities { get; set; } = new();
    public List<int> FacilityIds { get; set; } = new();   // multi-select
    public List<string> StatusFilters { get; set; } = new();   // multi-select
    // single-value helpers for backward compat
    public int? FacilityId => FacilityIds.Count == 1 ? FacilityIds[0] : null;
    public string? StatusFilter => StatusFilters.Count == 1 ? StatusFilters[0] : null;
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public List<ReconciliationRow> Rows { get; set; } = new();

    // Summary (computed from capped Rows list)
    public int TotalRowCount { get; set; }
    public int TotalClaims => Rows.Count;
    public bool IsCapped => false;   // cap removed — all rows displayed
    public decimal TotalSubmitted => Rows.Sum(r => r.SubmittedAmount ?? 0);
    public decimal TotalPaid => Rows.Sum(r => r.PaidAmount ?? 0);
    public decimal TotalOutstanding => TotalSubmitted - TotalPaid;
    public int PaidCount => Rows.Count(r => r.PaymentStatus == "Paid");
    public int PartialCount => Rows.Count(r => r.PaymentStatus == "Partial");
    public int RejectedCount => Rows.Count(r => r.PaymentStatus == "Rejected");
    public int PendingCount => Rows.Count(r => r.PaymentStatus == "Pending");
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
    public List<int> FacilityIds { get; set; } = new();   // multi-select
    public int? FacilityId => FacilityIds.Count == 1 ? FacilityIds[0] : null;
    public string? Portal { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? SearchText { get; set; }
    public List<PortalTransaction> Transactions { get; set; } = new();
    public int TotalCount { get; set; }
    public int FilesDownloadedCount { get; set; }   // records with FileDownloaded = true
    public int PendingFilesCount { get; set; }       // records awaiting file download
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}
