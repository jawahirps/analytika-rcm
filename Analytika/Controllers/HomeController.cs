using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Analytika.Controllers;

public class HomeController : Controller
{
    private const string DashboardSummaryCacheKey = "dashboard:summary:v1";
    private static readonly SemaphoreSlim DashboardSummaryLock = new(1, 1);
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDashboardService _dashboard;
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDashboardService dashboard,
        AppDbContext db,
        IMemoryCache cache,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dashboard = dashboard;
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet("/")]
    [HttpGet("/Home/Index")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Dashboard");
        return View(new LoginViewModel());
    }

    [HttpPost("/Home/Index")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user != null && !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "This account is inactive. Please contact an administrator.");
            return View(model);
        }

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", model.Email);
            return RedirectToAction("Dashboard");
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Account locked due to multiple failed attempts. Try again in 15 minutes.");
            return View(model);
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
    }

    // ── Facility Status Dashboard ─────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        if (User.IsInRole(AppRoles.Reporter))
            return RedirectToAction("ClaimSummaryReport", "ReportScheduler");
        return View(await _dashboard.BuildFacilityStatusAsync());
    }

    [Authorize(Roles = AppRoles.RcmAccess)]
    [HttpGet]
    public async Task<IActionResult> RCMDashboard(
        string tab = "Submissions",
        int? facilityId = null,
        string? receiver = null,
        string? payer = null,
        string? encounterType = null,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null)
    {
        var filters = new RcmDashboardFilters
        {
            FacilityId = facilityId,
            Receiver = receiver,
            Payer = payer,
            EncounterType = encounterType,
            DateFrom = dateFrom,
            DateTo = dateTo
        };

        return View(await _dashboard.BuildRcmDashboardAsync(tab, filters));
    }

    // ── Dashboard summary API (charts) ────────────────────────────

    [Authorize]
    [HttpGet("/api/dashboard/summary")]
    public async Task<IActionResult> DashboardSummary()
    {
        if (_cache.TryGetValue<object>(DashboardSummaryCacheKey, out var cached) && cached != null)
            return Json(cached);

        // IMemoryCache.GetOrCreateAsync does not serialize concurrent factories. The
        // semaphore makes a cold-cache burst one database aggregation, not one per user.
        await DashboardSummaryLock.WaitAsync(HttpContext.RequestAborted);
        try
        {
            if (_cache.TryGetValue<object>(DashboardSummaryCacheKey, out cached) && cached != null)
                return Json(cached);

            var payload = await ComputeDashboardSummaryAsync(HttpContext.RequestAborted);
            _cache.Set(DashboardSummaryCacheKey, payload, TimeSpan.FromMinutes(5));
            return Json(payload);
        }
        finally
        {
            DashboardSummaryLock.Release();
        }
    }

    private async Task<object> ComputeDashboardSummaryAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var d30 = now.AddDays(-30);
        var d60 = now.AddDays(-60);

        var daily = await _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.SyncedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        var byType = await _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        // One index-backed pass over the 60-day window replaces three separate
        // COUNT queries. This matters on the large PortalTransactions table and
        // keeps the values from the same database snapshot.
        var counts = await _db.PortalTransactions
            .AsNoTracking()
            .Where(t => t.SyncedAt >= d60)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Current = g.Count(t => t.SyncedAt >= d30),
                Previous = g.Count(t => t.SyncedAt < d30),
                Downloaded = g.Count(t => t.SyncedAt >= d30 && t.FileDownloaded)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var currentCount = counts?.Current ?? 0;
        var previousCount = counts?.Previous ?? 0;
        var downloaded = counts?.Downloaded ?? 0;

        double trend = previousCount > 0
            ? Math.Round((currentCount - previousCount) / (double)previousCount * 100.0, 1)
            : 0;

        return new
        {
            daily  = daily.Select(x => new { date = x.Date.ToString("MM/dd"), count = x.Count }).ToList(),
            byType = byType.Select(x => new { type = x.Type, count = x.Count }).ToList(),
            kpi    = new { currentCount, previousCount, trend, downloaded }
        };
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogOut()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index");
    }

    [HttpGet]
    public IActionResult Error()
    {
        return View();
    }
}
