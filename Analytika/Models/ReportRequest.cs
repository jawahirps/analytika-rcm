namespace Analytika.Models;

public class ReportRequest
{
    public int Id { get; set; }
    public string ReportId { get; set; } = string.Empty; // ANA-XXXXXXX
    public string ReportType { get; set; } = string.Empty;
    public int? BranchId { get; set; }
    public int? ReceiverId { get; set; }
    public int? PayerId { get; set; }
    public int? ClinicianId { get; set; }
    public int? DepartmentId { get; set; }
    public string? EncounterType { get; set; }
    public DateTime DateFrom { get; set; }
    public DateTime DateTo { get; set; }
    public string? SearchCriteria { get; set; }
    public string? Template { get; set; }
    public string FileFormat { get; set; } = "Excel";
    public string Status { get; set; } = "Pending";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? GeneratedAt { get; set; }
    public string? FilePath { get; set; }
    public string? RequestedBy { get; set; }

    public Facility? Branch { get; set; }
    public Receiver? Receiver { get; set; }
    public Payer? Payer { get; set; }
    public Clinician? Clinician { get; set; }
    public Department? DepartmentNav { get; set; }
}
