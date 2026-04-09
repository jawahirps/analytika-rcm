namespace Analytika.Models;

/// <summary>
/// A single claim extracted from a Remittance Advice XML file.
/// One remittance file → many RemittanceClaims.
/// </summary>
public class RemittanceClaim
{
    public int Id { get; set; }

    // Source transaction
    public int RemittanceTransactionId { get; set; }
    public PortalTransaction? RemittanceTransaction { get; set; }
    public int FacilityId { get; set; }
    public Facility? Facility { get; set; }

    // Claim identifiers
    public string ClaimId { get; set; } = string.Empty;       // <ID> from XML
    public string? PayerClaimId { get; set; }                  // <IDPayer>
    public string? PayerCode { get; set; }                     // <SenderID>
    public string? ClinicianLicense { get; set; }              // <Clinician> from first activity

    // Financials
    public decimal OriginalAmount { get; set; }                // sum of <Net>
    public decimal PaidAmount { get; set; }                    // sum of <PaymentAmount>
    public decimal DeniedAmount => OriginalAmount - PaidAmount;
    public bool IsFullyDenied => PaidAmount == 0 && OriginalAmount > 0;
    public bool IsPartiallyPaid => PaidAmount > 0 && PaidAmount < OriginalAmount;

    // Denial info
    public string? DenialCodesJson { get; set; }               // JSON array of unique denial codes
    public string? Comments { get; set; }                      // concatenated <Comments>
    public int ActivityCount { get; set; }

    // Settlement
    public string? SettlementDate { get; set; }
    public string? PaymentReference { get; set; }

    public DateTime ParsedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ResubmissionTask? Task { get; set; }
}
