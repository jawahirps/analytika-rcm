using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Xml.Linq;

namespace Analytika.Controllers;

public class HomeController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _db;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
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

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user != null && !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "This account is inactive. Please contact an administrator.");
            return View(model);
        }

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
        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };
        var logProjection = await _db.PortalFetchLogs
            .AsNoTracking()
            .Select(l => new { l.FacilityId, l.Status, l.Operation, l.FetchedAt })
            .ToListAsync();

        var latestMeaningful = logProjection
            .Where(l => meaningfulOps.Contains(l.Operation))
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var latestAny = logProjection
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var recentSuccess = logProjection
            .Where(l => l.Status == "Success" && l.FetchedAt >= cutoff)
            .Select(l => l.FacilityId)
            .ToHashSet();

        // Record & file counts per facility
        var txStats = await _db.PortalTransactions
            .AsNoTracking()
            .GroupBy(t => t.FacilityId)
            .Select(g => new
            {
                FacilityId = g.Key,
                Records = g.Count(),
                DownloadedFiles = g.Count(t => t.FileDownloaded),
                PendingFiles = g.Count(t => !t.FileDownloaded)
            })
            .ToListAsync();

        var credMap = credsByFacility.ToDictionary(x => x.FacilityId);
        var txMap = txStats.ToDictionary(x => x.FacilityId);
        var claimMap = await LoadClaimCountsByFacilityAsync();

        var rows = facilities.Select(f =>
        {
            credMap.TryGetValue(f.Id, out var cred);
            txMap.TryGetValue(f.Id, out var tx);
            claimMap.TryGetValue(f.Id, out var claimCount);

            latestMeaningful.TryGetValue(f.Id, out var mLog);
            latestAny.TryGetValue(f.Id, out var anyLog);
            var displayLog = mLog ?? anyLog;

            var effectiveStatus = recentSuccess.Contains(f.Id) ? "Success"
                                : mLog?.Status ?? anyLog?.Status;

            return new FacilityStatusRow
            {
                FacilityId = f.Id,
                FacilityName = f.Name,
                HasCredential = cred != null,
                Portal = cred != null ? string.Join(" · ", cred.Portals.Distinct()) : null,
                LastSyncTime = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                LastSyncStatus = effectiveStatus,
                RecordCount = tx?.Records ?? 0,
                ClaimCount = claimCount,
                FileCount = tx?.DownloadedFiles ?? 0,
                DownloadedFilesCount = tx?.DownloadedFiles ?? 0,
                PendingFilesCount = tx?.PendingFiles ?? 0,
            };
        })
        .OrderBy(r => r.Status)
        .ThenBy(r => r.FacilityName)
        .ToList();

        var vm = new FacilityStatusViewModel
        {
            Facilities = rows,
            TotalRecords = txStats.Sum(x => x.Records),
            TotalClaimCount = claimMap.Values.Sum(),
            TotalFiles = txStats.Sum(x => x.DownloadedFiles),
            LastSyncTime = logProjection.Count > 0
                ? logProjection.Max(l => l.FetchedAt).ToString("dd MMM yyyy HH:mm")
                : null
        };
        return View(vm);
    }

    private async Task<Dictionary<int, int>> LoadClaimCountsByFacilityAsync()
    {
        var result = new Dictionary<int, int>();
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT FacilityId,
                       COALESCE(SUM(
                           CASE
                               WHEN RecordStart > 0 AND RecordEnd > RecordStart
                               THEN CAST(substr(
                                   FileContentXml,
                                   RecordStart + length('<RecordCount>'),
                                   RecordEnd - (RecordStart + length('<RecordCount>'))
                               ) AS INTEGER)
                               ELSE 0
                           END
                       ), 0) AS ClaimCount
                FROM (
                    SELECT FacilityId,
                           FileContentXml,
                           instr(FileContentXml, '<RecordCount>') AS RecordStart,
                           instr(FileContentXml, '</RecordCount>') AS RecordEnd
                    FROM PortalTransactions
                    WHERE FileContentXml IS NOT NULL
                      AND length(FileContentXml) > 10
                      AND FileContentXml NOT LIKE '%Remittance.Advice%'
                      AND (
                          FileContentXml LIKE '%Claim.Submission%'
                          OR FileContentXml LIKE '%<ClaimSubmission%'
                      )
                ) ClaimXml
                GROUP BY FacilityId;";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var facilityId = reader.GetInt32(0);
                var claimCount = Convert.ToInt32(reader.GetValue(1));
                result[facilityId] = claimCount;
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return result;
    }

    [Authorize(Roles = AppRoles.RcmAccess)]
    [HttpGet]
    public IActionResult RCMDashboard(string tab = "Submissions")
    {
        var vm = BuildLocalDashboard(tab);
        return View(vm);
    }

    private static bool IsClaimSubmissionXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        if (xml.Contains("Remittance.Advice", StringComparison.OrdinalIgnoreCase)) return false;
        return xml.Contains("Claim.Submission", StringComparison.OrdinalIgnoreCase)
            || xml.Contains("<ClaimSubmission", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetHeaderRecordCount(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return 0;

        try
        {
            var doc = XDocument.Parse(xml);
            var header = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Header");
            if (header == null) return 0;

            var countText = header.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "RecordCount")
                ?.Value;

            return int.TryParse(countText, out var count) ? count : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static RCMDashboardViewModel BuildLocalDashboard(string tab)
    {
        var tabs = new List<string> { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
        var activeTab = tabs.Contains(tab, StringComparer.OrdinalIgnoreCase)
            ? tabs.First(t => t.Equals(tab, StringComparison.OrdinalIgnoreCase))
            : "Submissions";
        var stableFieldTitle = activeTab switch
        {
            "Submissions" => "Encounter Date",
            "Resubmissions" => "Encounter Date",
            "Remittance" => "Encounter Date",
            "Operations" => "Encounter Date",
            "Insurance" => "Encounter Date",
            "Department" => "Encounter Date",
            "Denials" => "Denial Code",
            "Clinicians" => "Department",
            _ => "Encounter Date"
        };
        var stableFieldDetail = activeTab switch
        {
            "Resubmissions" => "Resubmission exports are grouped by Encounter Date so all levels line up with submission data.",
            "Submissions" => "Shared submission anchor used across dashboard views.",
            "Denials" => "Denial dashboards are best read against denial code groupings.",
            "Clinicians" => "Clinician reporting is grouped by Department to keep rollups consistent.",
            _ => "Used to keep reporting aligned across dashboards."
        };

        var profiles = new Dictionary<string, (string Summary, int Seed)>
        {
            ["Submissions"] = ($"Claim submission volumes are stable with {stableFieldTitle} as the shared timeline field across exports.", 86),
            ["Resubmissions"] = ($"Resubmission queues are trending down as aging worklists clear, organized by {stableFieldTitle}.", 64),
            ["Remittance"] = ("Collections remain healthy with a focused reconciliation backlog.", 78),
            ["Denials"] = ("Denial pressure is concentrated in authorization and coding categories.", 52),
            ["Clinicians"] = ("Clinician productivity is balanced, with a few outliers needing follow-up.", 71),
            ["Operations"] = ("Operational throughput is steady and turnaround time is within target.", 83),
            ["Insurance"] = ("Payer performance is mixed; two networks are driving most exceptions.", 67),
            ["Department"] = ("Department-level activity is led by emergency, cardiology, and radiology.", 75)
        };

        var profile = profiles[activeTab];
        var seed = profile.Seed;

        return new RCMDashboardViewModel
        {
            ActiveTab = activeTab,
            Tabs = tabs,
            StableFieldTitle = stableFieldTitle,
            StableFieldDetail = stableFieldDetail,
            Summary = profile.Summary,
            RefreshedAt = DateTime.Now,
            Metrics =
            [
                new DashboardMetric { Label = "Total Claims", Value = $"{seed * 124:N0}", Delta = "+8.4%", Icon = "fa-file-medical", Tone = "teal" },
                new DashboardMetric { Label = "Net Value", Value = $"AED {seed * 18:N0}K", Delta = "+5.1%", Icon = "fa-coins", Tone = "gold" },
                new DashboardMetric { Label = "Clean Rate", Value = $"{Math.Min(seed + 9, 96)}%", Delta = "+2.7%", Icon = "fa-circle-check", Tone = "green" },
                new DashboardMetric { Label = "TAT", Value = $"{Math.Max(2, 14 - seed % 9)} days", Delta = "-1.3d", Icon = "fa-clock", Tone = "blue" }
            ],
            Trend =
            [
                new DashboardTrendPoint { Label = "Jan", Value = seed - 18 },
                new DashboardTrendPoint { Label = "Feb", Value = seed - 10 },
                new DashboardTrendPoint { Label = "Mar", Value = seed - 4 },
                new DashboardTrendPoint { Label = "Apr", Value = seed + 3 },
                new DashboardTrendPoint { Label = "May", Value = seed + 8 },
                new DashboardTrendPoint { Label = "Jun", Value = seed + 12 }
            ],
            Breakdown =
            [
                new DashboardBreakdownItem { Label = "Emergency", Value = seed + 10, Detail = "Highest activity" },
                new DashboardBreakdownItem { Label = "Cardiology", Value = seed - 4, Detail = "Within target" },
                new DashboardBreakdownItem { Label = "Radiology", Value = seed - 12, Detail = "Watchlist" },
                new DashboardBreakdownItem { Label = "Orthopedics", Value = seed - 18, Detail = "Improving" }
            ],
            Insights =
            [
                new DashboardInsight { Title = "Priority focus", Detail = $"{activeTab} exceptions are concentrated in three queues.", Status = "Action" },
                new DashboardInsight { Title = "Best performer", Detail = "Clean claims improved across the latest reporting cycle.", Status = "Good" },
                new DashboardInsight { Title = "Risk signal", Detail = "Aging work above seven days needs daily review.", Status = "Watch" }
            ]
        };
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
