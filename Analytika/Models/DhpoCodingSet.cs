namespace Analytika.Models;

/// <summary>
/// Lookup table seeded from DHPO coding set Excel files (eclaimlink.ae/CodingSets.aspx).
/// Category values: "Facility", "Clinician", "Payer" (covers both Insurance and TPA).
/// </summary>
public class DhpoCodingSet
{
    public int Id { get; set; }
    public string Category { get; set; } = "";   // Facility | Clinician | Payer
    public string Code { get; set; } = "";   // DHA-F-xxxx / DHA-P-xxxx / INS038 / TPA002
    public string Name { get; set; } = "";
    public string? SubType { get; set; }          // Insurance | TPA | Specialty | etc.
    public string? ExtraJson { get; set; }          // Any additional columns from Excel
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
