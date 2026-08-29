using Analytika.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Security;

/// <summary>
/// Answers "which tenant is this request acting for?".
///
/// Three distinct answers, and conflating them is how tenant leaks happen:
///   * a tenant id  → a customer user; every query must be scoped to it
///   * Unrestricted → a platform operator or a background tool (ParseAll, DownloadAll,
///                    report generation) that legitimately spans all tenants
///   * NoAccess     → signed in, but the tenant is Pending or Suspended: the account
///                    exists and may see billing/status pages, never clinical data
/// </summary>
public interface ITenantContext
{
    Task<TenantScope> GetScopeAsync();
}

public readonly record struct TenantScope(int? TenantId, bool Unrestricted, bool CanAccessData)
{
    public static TenantScope Platform() => new(null, true, true);
    public static TenantScope Denied() => new(null, false, false);
    public static TenantScope For(int tenantId, bool active) => new(tenantId, false, active);
}

public class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _http;
    private readonly UserManager<ApplicationUser> _users;
    private readonly AppDbContext _db;
    private TenantScope? _cached;

    public TenantContext(IHttpContextAccessor http, UserManager<ApplicationUser> users, AppDbContext db)
    {
        _http = http;
        _users = users;
        _db = db;
    }

    public async Task<TenantScope> GetScopeAsync()
    {
        if (_cached.HasValue) return _cached.Value;

        var principal = _http.HttpContext?.User;

        // No HTTP context at all = a background tool or a hosted service. These run as
        // the system and must see every tenant, otherwise the nightly sync/parse would
        // silently process only one customer's data.
        if (principal == null)
            return (_cached = TenantScope.Platform()).Value;

        if (principal.Identity?.IsAuthenticated != true)
            return (_cached = TenantScope.Denied()).Value;

        var user = await _users.GetUserAsync(principal);
        if (user == null)
            return (_cached = TenantScope.Denied()).Value;

        // Platform operators (your staff) carry no tenant and support every customer.
        if (user.TenantId == null)
            return (_cached = TenantScope.Platform()).Value;

        var status = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == user.TenantId.Value)
            .Select(t => t.Status)
            .FirstOrDefaultAsync();

        return (_cached = TenantScope.For(user.TenantId.Value, TenantStatus.CanAccessData(status))).Value;
    }
}
