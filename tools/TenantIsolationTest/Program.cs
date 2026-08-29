using Analytika.Models;
using Analytika.Modules;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

// Phase 0 acceptance criterion, from the onboarding plan:
//   "A signed-up tenant cannot read another tenant's rows even with a crafted request —
//    proven by test, not by inspection."
//
// Builds two tenants with a facility each in a scratch database, then asks
// FacilityScopeService what each user may see — including the crafted-request case, where
// a user submits a facility id belonging to somebody else.
//
// Exits non-zero if any assertion fails, so it can gate a deploy.

var dbPath = Path.Combine(Path.GetTempPath(), $"bix-tenant-test-{Guid.NewGuid():N}.db");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Data:Dir"] = Path.GetDirectoryName(dbPath),
    ["StartupMaintenance:SeedDataOnStartup"] = "false",
});
builder.Services.AddDataProtection();
builder.Services.AddControllersWithViews();
builder.Services.AddAnalytikaModules(builder.Configuration, dbPath, false, false, false);

var app = builder.Build();

int failures = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
    if (!ok) failures++;
}

using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    SqliteSchemaService.EnsureSchema(db, createIndexes: false);

    var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var r in new[] { AppRoles.Admin, AppRoles.FacilityAdmin, AppRoles.Analyst })
        if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

    // ── two tenants, one facility each ───────────────────────────────────────
    var alpha = new Tenant { Name = "Alpha Clinics", Slug = "alpha", Status = TenantStatus.Active };
    var beta = new Tenant { Name = "Beta Medical", Slug = "beta", Status = TenantStatus.Active };
    var pending = new Tenant { Name = "Pending Co", Slug = "pending", Status = TenantStatus.Pending };
    db.Tenants.AddRange(alpha, beta, pending);
    await db.SaveChangesAsync();

    var facA = new Facility { Name = "Alpha Clinic One", IsActive = true, TenantId = alpha.Id };
    var facB = new Facility { Name = "Beta Clinic One", IsActive = true, TenantId = beta.Id };
    db.Facilities.AddRange(facA, facB);
    await db.SaveChangesAsync();

    var users = sp.GetRequiredService<UserManager<ApplicationUser>>();

    async Task<ApplicationUser> MakeUser(string email, int? tenantId, string role, params int[] facilityIds)
    {
        var u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IsActive = true, TenantId = tenantId };
        var res = await users.CreateAsync(u, "Test@Passw0rd!");
        if (!res.Succeeded) throw new Exception(string.Join("; ", res.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(u, role);
        foreach (var fid in facilityIds) db.UserFacilities.Add(new UserFacility { UserId = u.Id, FacilityId = fid });
        await db.SaveChangesAsync();
        return u;
    }

    var alphaAdmin = await MakeUser("admin@alpha.test", alpha.Id, AppRoles.Admin);
    var betaAdmin = await MakeUser("admin@beta.test", beta.Id, AppRoles.Admin);
    var alphaAnalyst = await MakeUser("analyst@alpha.test", alpha.Id, AppRoles.Analyst, facA.Id);
    var pendingUser = await MakeUser("new@pending.test", pending.Id, AppRoles.Admin);
    var platformOperator = await MakeUser("ops@bix.test", null, AppRoles.Admin);

    ClaimsPrincipal As(ApplicationUser u, string role) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, u.Id),
        new Claim(ClaimTypes.Name, u.UserName!),
        new Claim(ClaimTypes.Role, role),
    }, "test"));

    Console.WriteLine("Tenant isolation — FacilityScopeService");
    Console.WriteLine(new string('=', 70));

    // TenantContext reads the principal from HttpContext and caches the answer for the
    // lifetime of the scope. Running every user through one scope would both bypass
    // tenancy (no HttpContext = background tool = unrestricted, by design) and reuse the
    // first user's cached scope. So: one DI scope per user, with a real HttpContext.
    async Task<T> AsRequest<T>(ApplicationUser u, string role, Func<FacilityScopeService, Task<T>> body)
    {
        using var reqScope = app.Services.CreateScope();
        var accessor = reqScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = As(u, role),
            RequestServices = reqScope.ServiceProvider,
        };
        return await body(reqScope.ServiceProvider.GetRequiredService<FacilityScopeService>());
    }

    // A tenant administrator sees their own tenant's facilities and nothing else.
    // null means "unrestricted", so every isolation assertion must reject null explicitly
    // rather than treating it as "did not contain the foreign id".
    var alphaScope = await AsRequest(alphaAdmin, AppRoles.Admin, s => s.GetAllowedFacilityIdsAsync(As(alphaAdmin, AppRoles.Admin)));
    Check("Alpha admin is scoped (not unrestricted)", alphaScope != null);
    Check("Alpha admin sees Alpha's facility", alphaScope?.Contains(facA.Id) == true);
    Check("Alpha admin CANNOT see Beta's facility", alphaScope != null && !alphaScope.Contains(facB.Id),
          $"scope = [{(alphaScope == null ? "UNRESTRICTED" : string.Join(",", alphaScope))}]");

    var betaScope = await AsRequest(betaAdmin, AppRoles.Admin, s => s.GetAllowedFacilityIdsAsync(As(betaAdmin, AppRoles.Admin)));
    Check("Beta admin CANNOT see Alpha's facility", betaScope != null && !betaScope.Contains(facA.Id),
          $"scope = [{(betaScope == null ? "UNRESTRICTED" : string.Join(",", betaScope))}]");

    // The crafted request: asking directly for a facility that belongs to another tenant.
    Check("Alpha admin denied direct access to Beta facility id",
          !await AsRequest(alphaAdmin, AppRoles.Admin, s => s.CanAccessAsync(As(alphaAdmin, AppRoles.Admin), facB.Id)));
    Check("Alpha analyst denied direct access to Beta facility id",
          !await AsRequest(alphaAnalyst, AppRoles.Analyst, s => s.CanAccessAsync(As(alphaAnalyst, AppRoles.Analyst), facB.Id)));
    Check("Alpha analyst allowed its own assigned facility",
          await AsRequest(alphaAnalyst, AppRoles.Analyst, s => s.CanAccessAsync(As(alphaAnalyst, AppRoles.Analyst), facA.Id)));

    // A tenant that has not been approved yet must read nothing at all.
    var pendingScope = await AsRequest(pendingUser, AppRoles.Admin, s => s.GetAllowedFacilityIdsAsync(As(pendingUser, AppRoles.Admin)));
    Check("Pending tenant sees no facilities", pendingScope != null && pendingScope.Count == 0,
          $"scope = [{(pendingScope == null ? "UNRESTRICTED" : string.Join(",", pendingScope))}]");
    Check("Pending tenant denied a real facility id",
          !await AsRequest(pendingUser, AppRoles.Admin, s => s.CanAccessAsync(As(pendingUser, AppRoles.Admin), facA.Id)));

    // The platform operator (no tenant) keeps today's unrestricted behaviour.
    var opScope = await AsRequest(platformOperator, AppRoles.Admin, s => s.GetAllowedFacilityIdsAsync(As(platformOperator, AppRoles.Admin)));
    Check("Platform operator is unrestricted", opScope == null);

    // Clamping a requested selection must drop foreign ids rather than honour them.
    // This is the crafted-request path: the ids arrive from a form post, not a dropdown.
    var clamped = await AsRequest(alphaAdmin, AppRoles.Admin,
        s => s.ClampAsync(As(alphaAdmin, AppRoles.Admin), new List<int> { facA.Id, facB.Id }));
    Check("Clamping a crafted selection drops the foreign facility",
          clamped.Contains(facA.Id) && !clamped.Contains(facB.Id),
          $"clamped = [{string.Join(",", clamped)}]");
}

Console.WriteLine(new string('=', 70));
Console.WriteLine(failures == 0
    ? "tenant isolation holds"
    : $"{failures} assertion(s) FAILED — tenants are not isolated");

try { File.Delete(dbPath); } catch { }
return failures == 0 ? 0 : 1;
