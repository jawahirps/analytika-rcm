using Analytika.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Analytika.Security;

/// <summary>
/// Single source of truth for "which facilities may this user see".
///
/// Admin sees everything. EVERY other user is isolated to the facilities explicitly
/// assigned to them (UserFacilities) — a user with no assignment sees nothing, which is
/// the safe default. Callers must use this for BOTH display filtering and server-side
/// enforcement: filtering a dropdown is not enough, since a form/query value can be
/// tampered with.
/// </summary>
public class FacilityScopeService
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITenantContext _tenant;

    public FacilityScopeService(AppDbContext db, UserManager<ApplicationUser> users, ITenantContext tenant)
    {
        _db = db;
        _users = users;
        _tenant = tenant;
    }

    /// <summary>null = unrestricted (Admin). Otherwise the exact facility IDs allowed (possibly empty).</summary>
    public async Task<List<int>?> GetAllowedFacilityIdsAsync(ClaimsPrincipal principal)
    {
        // ── Tenant boundary, applied before any role logic ───────────────────────
        // Roles are scoped WITHIN a tenant: a customer's Admin administers their own
        // organisation, not the platform. Deciding "Admin ⇒ sees everything" before
        // checking tenancy is precisely how one customer would end up reading another's
        // claims, so the tenant test comes first and cannot be skipped by a role.
        var scope = await _tenant.GetScopeAsync();

        if (!scope.CanAccessData)
            return new List<int>();          // Pending or Suspended tenant: no clinical data

        if (!scope.Unrestricted && scope.TenantId is int tenantId)
        {
            var tenantFacilities = await _db.Facilities.AsNoTracking()
                .Where(f => f.TenantId == tenantId)
                .Select(f => f.Id)
                .ToListAsync();

            // A tenant administrator gets every facility their organisation owns.
            if (principal.IsInRole(AppRoles.Admin))
                return tenantFacilities;

            var assigned = await GetAssignedFacilityIdsAsync(principal);
            // Intersect: an assignment that points outside the tenant is never honoured.
            return assigned.Where(tenantFacilities.Contains).ToList();
        }

        // Platform operator (no tenant) — unrestricted, as before.
        if (principal.IsInRole(AppRoles.Admin))
            return null;

        var user = await _users.GetUserAsync(principal);
        if (user == null)
            return new List<int>();

        return await GetAssignedFacilityIdsAsync(principal);
    }

    /// <summary>Raw UserFacilities assignments, before any tenant intersection.</summary>
    private async Task<List<int>> GetAssignedFacilityIdsAsync(ClaimsPrincipal principal)
    {
        var user = await _users.GetUserAsync(principal);
        if (user == null) return new List<int>();

        return await _db.UserFacilities.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .Select(x => x.FacilityId)
            .Distinct()
            .ToListAsync();
    }

    public async Task<bool> IsUnrestrictedAsync(ClaimsPrincipal principal)
        => await GetAllowedFacilityIdsAsync(principal) is null;

    /// <summary>True when the user may see this facility.</summary>
    public async Task<bool> CanAccessAsync(ClaimsPrincipal principal, int facilityId)
    {
        var allowed = await GetAllowedFacilityIdsAsync(principal);
        return allowed is null || allowed.Contains(facilityId);
    }

    /// <summary>
    /// Narrow a requested facility selection to what the user is allowed. Unrestricted users
    /// get their request unchanged; scoped users get the intersection, and an empty/absent
    /// request falls back to their full allowed set (never "all").
    /// </summary>
    public async Task<List<int>> ClampAsync(ClaimsPrincipal principal, IEnumerable<int>? requested)
    {
        var req = (requested ?? Enumerable.Empty<int>()).Where(i => i > 0).Distinct().ToList();
        var allowed = await GetAllowedFacilityIdsAsync(principal);
        if (allowed is null) return req;                 // Admin: honour request (empty = all)
        if (req.Count == 0) return allowed;              // scoped: default to own facilities
        return req.Where(allowed.Contains).ToList();     // scoped: intersection only
    }
}
