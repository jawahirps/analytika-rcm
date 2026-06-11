using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Controllers;

public class HomeController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDashboardService _dashboard;
    private readonly AppDbContext _db;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDashboardService dashboard,
        AppDbContext db,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dashboard = dashboard;
        _db = db;
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
            model.Email, model.Password, isPersistent: true, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", model.Email);
            return RedirectToAction("Dashboard");
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
    }

    // ── Facility Status Dashboard ─────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
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
        var now         = DateTime.UtcNow;
        var d30         = now.AddDays(-30);
        var d60         = now.AddDays(-60);

        // Daily transaction counts for sparkline
        var daily = await _db.PortalTransactions
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.SyncedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync();

        // Type breakdown for donut chart
        var byType = await _db.PortalTransactions
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        // KPI trend
        var currentCount  = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d30);
        var previousCount = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d60 && t.SyncedAt < d30);
        var downloaded    = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d30 && t.FileDownloaded);

        double trend = previousCount > 0
            ? Math.Round((currentCount - previousCount) / (double)previousCount * 100.0, 1)
            : 0;

        return Json(new
        {
            daily    = daily.Select(x => new { date = x.Date.ToString("MM/dd"), count = x.Count }),
            byType   = byType.Select(x => new { type = x.Type, count = x.Count }),
            kpi      = new { currentCount, previousCount, trend, downloaded }
        });
    }

    [HttpPost]
    [Authorize]
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
