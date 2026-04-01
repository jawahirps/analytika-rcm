namespace Analytika.Models;

public class PortalTransaction
{
    public int Id { get; set; }
    public string Portal { get; set; } = string.Empty;        // "DHA" | "RHA"
    public int FacilityId { get; set; }
    public Facility Facility { get; set; } = null!;

    // Transaction identity
    public string TransactionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Direction { get; set; }                    // "Sent" | "Received"

    // File reference (from SearchTransactions / GetNewTransactions)
    public string? FileId { get; set; }                       // The fileName field used as fileId in DownloadTransactionFile
    public string? FileName { get; set; }

    // Downloaded file data
    public bool FileDownloaded { get; set; } = false;         // Whether DownloadTransactionFile was called
    public string? FileContentXml { get; set; }               // Parsed/raw XML from the downloaded file
    public long? FileSizeBytes { get; set; }
    public DateTime? FileDownloadedAt { get; set; }

    // Transaction details
    public string? TransactionDate { get; set; }
    public string? Payer { get; set; }
    public string? Amount { get; set; }
    public string? RawXml { get; set; }                       // Raw transaction row XML from search response

    // Sync metadata
    public string Operation { get; set; } = string.Empty;     // SearchTransactions | GetNewTransactions | GetClaims etc.
    public string? SyncPeriod { get; set; }                   // e.g. "2024-03"
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}
