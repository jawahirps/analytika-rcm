namespace Analytika.Models;

public class Facility
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Official DHPO/eClaimLink facility license identity, matched from the imported
    // facility list. Kept alongside the short display Name (non-destructive).
    public string? FullName { get; set; }      // official license name
    public string? LicenseCode { get; set; }   // DHA-F-xxxxx license code

    /// <summary>
    /// Owning tenant. This is the single hinge of tenant isolation: every clinical table
    /// carries FacilityId, so scoping a query to a tenant means scoping through here.
    /// Nullable only so existing rows migrate cleanly; the schema step assigns them all
    /// to the founding tenant immediately.
    /// </summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
}
