namespace Analytika.Models;

/// <summary>
/// A customer organisation. One tenant owns one or more facilities; every piece of
/// clinical data in the system reaches its tenant through <see cref="Facility"/>.
///
/// Why tenancy hangs off Facility rather than a TenantId column on every table:
/// PortalTransactions holds ~1.17M rows with XML blobs stored inline. SQLite rewrites
/// the WHOLE row when any column changes, so back-filling a TenantId there would rewrite
/// tens of gigabytes — measured earlier on this database at 0.03 MB/s with the WAL
/// growing past 46 GB before the attempt was abandoned. Every tenant-owned table already
/// carries FacilityId, so scoping through Facility is both correct and free.
/// </summary>
public class Tenant
{
    public int Id { get; set; }

    /// <summary>Organisation name as the customer would write it.</summary>
    public string Name { get; set; } = "";

    /// <summary>URL-safe identifier, unique. Used for tenant-addressed routes later.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Regulator licence captured at sign-up (DHA-F-… or MOH numeric).</summary>
    public string? LicenseNumber { get; set; }

    /// <summary>
    /// Pending → signed up, not yet verified: may sign in, may NOT reach clinical data.
    /// Active  → verified by an operator; full access.
    /// Suspended → retained but locked out (non-payment, offboarding, investigation).
    /// </summary>
    public string Status { get; set; } = TenantStatus.Pending;

    public string? ContactEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public ICollection<Facility> Facilities { get; set; } = new List<Facility>();
}

public static class TenantStatus
{
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Suspended = "Suspended";

    /// <summary>Only an Active tenant may read or write clinical data.</summary>
    public static bool CanAccessData(string? status) =>
        string.Equals(status, Active, StringComparison.OrdinalIgnoreCase);
}
