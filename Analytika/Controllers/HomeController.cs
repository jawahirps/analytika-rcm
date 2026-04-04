using Analytika.Models;
using Analytika.Models.ViewModels;
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
    private readonly IPowerBIService _powerBIService;
    private readonly AppDbContext _db;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IPowerBIService powerBIService,
        AppDbContext db,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _powerBIService = powerBIService;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Dashboard");
        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);

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
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        // Active credentials per facility
        var credsByFacility = await _db.PortalCredentials
            .Where(c => c.IsActive)
            .GroupBy(c => c.FacilityId)
            .Select(g => new { FacilityId = g.Key, Portals = g.Select(c => c.Portal).ToList() })
            .ToListAsync();

        // Latest MEANINGFUL sync log per facility
        // Exclude individual "SearchTransactions" calls — transient failures from those
        // should not drag the facility status to Degraded.
        // Priority: CronSync / MonthWiseSync / BulkSave / SyncAll2Y ops.
        // Fall back to any log if no meaningful one exists.
        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };

        var allLogs = await _db.PortalFetchLogs.ToListAsync();

        var latestMeaningful = allLogs
            .Where(l => meaningfulOps.Contains(l.Operation))
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var latestAny = allLogs
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        // Was there any successful sync in the last 48 hours?
        var cutoff = DateTime.UtcNow.AddHours(-48);
        var recentSuccess = allLogs
            .Where(l => l.Status == "Success" && l.FetchedAt >= cutoff)
            .Select(l => l.FacilityId)
            .ToHashSet();

        // Record & file counts per facility
        var txStats = await _db.PortalTransactions
            .GroupBy(t => t.FacilityId)
            .Select(g => new
            {
                FacilityId  = g.Key,
                Records     = g.Count(),
                Files       = g.Count(t => t.FileDownloaded)
            })
            .ToListAsync();

        var credMap = credsByFacility.ToDictionary(x => x.FacilityId);
        var txMap   = txStats.ToDictionary(x => x.FacilityId);

        var rows = facilities.Select(f =>
        {
            credMap.TryGetValue(f.Id, out var cred);
            txMap  .TryGetValue(f.Id, out var tx);

            // Use meaningful log for status; fall back to any log for display time
            latestMeaningful.TryGetValue(f.Id, out var mLog);
            latestAny       .TryGetValue(f.Id, out var anyLog);
            var displayLog = mLog ?? anyLog;

            // Status: if there's a recent success anywhere, treat as Connected
            var effectiveStatus = recentSuccess.Contains(f.Id) ? "Success"
                                : mLog?.Status ?? anyLog?.Status;

            return new FacilityStatusRow
            {
                FacilityId      = f.Id,
                FacilityName    = f.Name,
                HasCredential   = cred != null,
                Portal          = cred != null ? string.Join(" · ", cred.Portals.Distinct()) : null,
                LastSyncTime    = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                LastSyncStatus  = effectiveStatus,
                RecordCount     = tx?.Records ?? 0,
                FileCount       = tx?.Files ?? 0,
            };
        })
        .OrderBy(r => r.Status)
        .ThenBy(r => r.FacilityName)
        .ToList();

        var vm = new FacilityStatusViewModel
        {
            Facilities   = rows,
            TotalRecords = txStats.Sum(x => x.Records),
            TotalFiles   = txStats.Sum(x => x.Files),
            LastSyncTime = allLogs.Count > 0
                ? allLogs.Max(l => l.FetchedAt).ToString("dd MMM yyyy HH:mm")
                : null
        };
        return View(vm);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> RCMDashboard(string tab = "Submissions")
    {
        var embed = await _powerBIService.GetEmbedConfigAsync(tab);
        var vm = new RCMDashboardViewModel
        {
            ActiveTab = tab,
            EmbedToken = embed?.AccessToken,
            EmbedUrl = embed?.EmbedUrl,
            ReportId = embed?.ReportId
        };
        return View(vm);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetEmbedToken(string tab)
    {
        var config = await _powerBIService.GetEmbedConfigAsync(tab);
        if (config == null) return NotFound();
        return Json(new
        {
            accessToken = config.AccessToken,
            embedUrl = config.EmbedUrl,
            reportId = config.ReportId
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
