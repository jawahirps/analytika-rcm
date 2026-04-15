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
    [ResponseCache(Duration = 60, VaryByQueryKeys = new string[] { })]
    public async Task<IActionResult> Dashboard()
    {
        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };
        var cutoff = DateTime.UtcNow.AddHours(-48);

        // Consolidate into 2 queries instead of 4
        // Query 1: All facility data in single round-trip
        var facilities = await _db.Facilities
            .Where(f => f.IsActive)
            .Select(f => new
            {
                f.Id,
                f.Name,
                Portals = _db.PortalCredentials
                    .Where(c => c.FacilityId == f.Id && c.IsActive)
                    .Select(c => c.Portal)
                    .Distinct()
                    .ToList()
            })
            .ToListAsync();

        // Query 2: Sync logs and transaction stats in single round-trip
        var syncStats = await (from l in _db.PortalFetchLogs
            group l by l.FacilityId into g
            select new
            {
                FacilityId = g.Key,
                LatestMeaningful = g
                    .Where(x => meaningfulOps.Contains(x.Operation))
                    .OrderByDescending(x => x.FetchedAt)
                    .FirstOrDefault(),
                LatestAny = g.OrderByDescending(x => x.FetchedAt).FirstOrDefault(),
                HasRecentSuccess = g.Any(x => x.Status == "Success" && x.FetchedAt >= cutoff)
            })
            .ToListAsync();

        var syncMap = syncStats.ToDictionary(s => s.FacilityId);

        // Query 3: Transaction stats (optimized count query)
        var txStats = await _db.PortalTransactions
            .GroupBy(t => t.FacilityId)
            .Select(g => new
            {
                FacilityId      = g.Key,
                TotalCount      = g.Count(),
                DownloadedCount = g.Count(t => t.FileDownloaded)
            })
            .ToListAsync();

        var txMap = txStats.ToDictionary(s => s.FacilityId);

        var rows = facilities.Select(f =>
        {
            syncMap.TryGetValue(f.Id, out var sync);
            txMap.TryGetValue(f.Id, out var tx);

            var displayLog = sync?.LatestMeaningful ?? sync?.LatestAny;
            var effectiveStatus = sync?.HasRecentSuccess == true ? "Success" : sync?.LatestMeaningful?.Status;
            var totalDownloaded = tx?.DownloadedCount ?? 0;
            var totalTx = tx?.TotalCount ?? 0;

            return new FacilityStatusRow
            {
                FacilityId            = f.Id,
                FacilityName          = f.Name,
                HasCredential         = f.Portals.Count > 0,
                Portal                = f.Portals.Count > 0 ? string.Join(" · ", f.Portals.Distinct()) : null,
                LastSyncTime          = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                LastSyncStatus        = effectiveStatus,
                RecordCount           = totalTx,
                FileCount             = totalDownloaded,
                DownloadedFilesCount  = totalDownloaded,
                PendingFilesCount     = totalTx - totalDownloaded,
            };
        })
        .OrderBy(r => r.Status)
        .ThenBy(r => r.FacilityName)
        .ToList();

        var totalTxCount = txStats.Sum(x => x.TotalCount);
        var totalDlCount = txStats.Sum(x => x.DownloadedCount);

        var vm = new FacilityStatusViewModel
        {
            Facilities   = rows,
            TotalRecords = totalTxCount,
            TotalFiles   = totalDlCount,
            LastSyncTime = syncStats.SelectMany(s => new[] { s.LatestMeaningful, s.LatestAny })
                .Where(s => s != null)
                .Max(s => s!.FetchedAt)
                .ToString("dd MMM yyyy HH:mm")
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
