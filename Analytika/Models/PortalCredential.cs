namespace Analytika.Models;

public class PortalCredential
{
    public int Id { get; set; }
    public string Portal { get; set; } = string.Empty; // "RHA" or "DHA"
    public int FacilityId { get; set; }
    public string? CredentialName { get; set; }        // user-defined display label
    public string Username { get; set; } = string.Empty;
    public string PasswordEncrypted { get; set; } = string.Empty;
    public string? ApiBaseUrl { get; set; }
    public string? LicenseCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public Facility? Facility { get; set; }
}
