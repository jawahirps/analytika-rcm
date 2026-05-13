namespace Analytika.Models;

/// <summary>
/// Claim-level cache extracted from downloaded DHA XML files.
/// Reports read this table so large XML blobs do not need to be parsed repeatedly.
/// </summary>
public class XmlParsedRecord
{
    public int Id { get; set; }

    public int PortalTransactionId { get; set; }
    public PortalTransaction? PortalTransaction { get; set; }

    public int FacilityId { get; set; }
    public Facility? Facility { get; set; }

    public string RecordKind { get; set; } = ""; // Submission | Remittance
    public string ClaimId { get; set; } = "";
    public string? FileName { get; set; }
    public string? FileId { get; set; }
    public string? TransactionDate { get; set; }

    public string? SenderId { get; set; }
    public string? ReceiverId { get; set; }
    public string? ReceiverName { get; set; }
    public string? PayerId { get; set; }
    public string? PayerName { get; set; }
    public string? PatientId { get; set; }
    public string? MemberId { get; set; }

    public string? TreatmentDate { get; set; }
    public string? TreatmentDateEnd { get; set; }
    public string? DateOfAdmission { get; set; }
    public string? SubmissionDate { get; set; }
    public string? EncounterType { get; set; }
    public string? Clinician { get; set; }
    public string? ServiceYear { get; set; }
    public string? ServiceMonth { get; set; }

    public decimal NetAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public int ActivityCount { get; set; }

    public string? PaymentReference { get; set; }
    public string? SettlementDate { get; set; }
    public string? DenialCodesJson { get; set; }
    public string? Comments { get; set; }
    public string? IdPayer { get; set; }
    public string? ResubmissionType { get; set; }
    public string? PrincipalDiagnosis { get; set; }

    public bool IsMatched { get; set; }
    public bool ReadyForReport { get; set; } = true;
    public string? Notes { get; set; }

    public DateTime ParsedAt { get; set; } = DateTime.UtcNow;
    public DateTime? MatchedAt { get; set; }
}
