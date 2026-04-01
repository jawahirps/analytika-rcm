using Microsoft.AspNetCore.Identity;

namespace Analytika.Models;

public class ApplicationUser : IdentityUser
{
    public string? FullName { get; set; }
    public string? Department { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public string UserType { get; set; } = "Global"; // "Global" or "Facility"
    public ICollection<UserFacility> UserFacilities { get; set; } = new List<UserFacility>();
    public ICollection<UserReportAccess> ReportAccesses { get; set; } = new List<UserReportAccess>();
}
