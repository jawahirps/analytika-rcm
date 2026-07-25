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

    public FacilityScopeService(AppDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    /// <summary>null = unrestricted (Admin). Otherwise the exact facility IDs allowed (possibly empty).</summary>
    public async Task<List<int>?> GetAllowedFacilityIdsAsync(ClaimsPrincipal principal)
    {
        if (principal.IsInRole(AppRoles.Admin))
            return null;

        var user = await _users.GetUserAsync(principal);
        if (user == null)
            return new List<int>();

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
