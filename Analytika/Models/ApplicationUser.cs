using Microsoft.AspNetCore.Identity;

namespace Analytika.Models;

public class ApplicationUser : IdentityUser
{
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public string UserType { get; set; } = "Global"; // "Global" or "Facility"

    /// <summary>
    /// Owning tenant. Null means a platform operator (your staff), who are not bound to
    /// one customer. Every self-provisioned user gets a tenant at sign-up.
    /// </summary>
    public int? TenantId { get; set; }
    public ICollection<UserFacility> UserFacilities { get; set; } = new List<UserFacility>();
    public ICollection<UserReportAccess> ReportAccesses { get; set; } = new List<UserReportAccess>();
}
