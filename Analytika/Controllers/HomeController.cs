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
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDashboardService _dashboard;
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HomeController> _logger;
    private readonly FacilityScopeService _scope;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDashboardService dashboard,
        AppDbContext db,
        IMemoryCache cache,
        ILogger<HomeController> logger,
        FacilityScopeService scope)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dashboard = dashboard;
        _db = db;
        _cache = cache;
        _logger = logger;
        _scope = scope;
    }

    // Public marketing landing page. Authenticated users skip straight to the dashboard.
    [HttpGet("/")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Dashboard");
        return View("Landing");
    }

    // Login form. Lives at /login now that / is the marketing landing.
    // /Home/Index and /Home/Login are kept for back-compat (old bookmarks,
    // Program.cs inactive-user redirect, LogOut redirect).
    [HttpGet("/login")]
    [HttpGet("/Home/Index")]
    [HttpGet("/Home/Login")]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Dashboard");
        return View("Index", new LoginViewModel());
    }

    [HttpPost("/login")]
    [HttpPost("/Home/Index")]
    [HttpPost("/Home/Login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View("Index", model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user != null && !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "This account is inactive. Please contact an administrator.");
            return View("Index", model);
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
            return View("Index", model);
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View("Index", model);
    }

    // ── Facility Status Dashboard ─────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        if (User.IsInRole(AppRoles.Reporter))
            return RedirectToAction("ClaimSummaryReport", "ReportScheduler");

        var vm = await _dashboard.BuildFacilityStatusAsync();

        // Facility isolation: everyone except Admin sees ONLY their assigned facilities.
        // The underlying aggregate is cached globally, so narrow the view per user here
        // and recompute the roll-ups from the visible rows.
        var allowed = await _scope.GetAllowedFacilityIdsAsync(User);
        if (allowed is not null)
        {
            vm.Facilities = vm.Facilities.Where(f => allowed.Contains(f.FacilityId)).ToList();
            vm.TotalRecords = vm.Facilities.Sum(f => f.RecordCount);
            vm.TotalClaimCount = vm.Facilities.Sum(f => f.ClaimCount);
            vm.TotalFiles = vm.Facilities.Sum(f => f.DownloadedFilesCount);
        }
        return View(vm);
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
        // Facility isolation: the facilityId arrives from the query string, so it must be
        // validated — a scoped user must not be able to view another facility by editing
        // the URL. Non-admins are forced onto one of their own facilities.
        var allowed = await _scope.GetAllowedFacilityIdsAsync(User);
        if (allowed is not null)
        {
            if (allowed.Count == 0)
                return View("NoFacilityAccess");
            if (facilityId is null || !allowed.Contains(facilityId.Value))
                facilityId = allowed[0];
        }

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
        // 5-minute cache — the 5 aggregations on a 45GB SQLite DB cost ~1s on cold path.
        // Invalidate on portal sync via _cache.Remove("dashboard:summary:v1") if real-time freshness needed.
        var payload = await _cache.GetOrCreateAsync("dashboard:summary:v1", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await ComputeDashboardSummaryAsync();
        });

        return Json(payload);
    }

    private async Task<object> ComputeDashboardSummaryAsync()
    {
        var now = DateTime.UtcNow;
        var d30 = now.AddDays(-30);
        var d60 = now.AddDays(-60);

        var daily = await _db.PortalTransactions
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.SyncedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var byType = await _db.PortalTransactions
            .Where(t => t.SyncedAt >= d30)
            .GroupBy(t => t.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        var currentCount  = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d30);
        var previousCount = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d60 && t.SyncedAt < d30);
        var downloaded    = await _db.PortalTransactions.CountAsync(t => t.SyncedAt >= d30 && t.FileDownloaded);

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
        return RedirectToAction("Login");
    }

    // ─── Self-service password reset ──────────────────────────────────────────
    // Previously only an administrator could reset a password, so every forgotten
    // password became a support request. Identity already issues single-use,
    // time-limited reset tokens; this exposes that properly.

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, [FromServices] IEmailService email)
    {
        if (!ModelState.IsValid) return View(model);

        // Always render the same confirmation, whether or not the account exists.
        // Diverging here would turn this form into an account-enumeration oracle.
        var user = await _userManager.FindByEmailAsync(model.Email)
                   ?? await _userManager.FindByNameAsync(model.Email);

        if (user != null && user.IsActive)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var link = Url.Action(nameof(ResetPassword), "Home",
                new { email = user.Email ?? user.UserName, token }, Request.Scheme);

            var body = $@"
                <p>Hello{(string.IsNullOrWhiteSpace(user.FullName) ? "" : " " + user.FullName)},</p>
                <p>Use the link below to choose a new Bix password. It works once and expires shortly.</p>
                <p><a href=""{link}"">Choose a new password</a></p>
                <p>If you did not request this, no action is needed — your current password still works.</p>";

            try { await email.SendEmailAsync(user.Email ?? user.UserName!, "Reset your Bix password", body); }
            catch (Exception ex)
            {
                // Never surface mail-transport detail to an anonymous caller.
                _logger.LogError(ex, "Password reset email failed for {User}", user.Id);
            }
        }
        else
        {
            _logger.LogInformation("Password reset requested for unknown or inactive address");
        }

        return RedirectToAction(nameof(ForgotPasswordSent));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPasswordSent() => View();

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return View("ResetPasswordInvalid");
        return View(new ResetPasswordViewModel { Email = email, Token = token });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email)
                   ?? await _userManager.FindByNameAsync(model.Email);
        if (user == null)
            return RedirectToAction(nameof(ResetPasswordDone));   // same outcome, no enumeration

        var result = await _userManager.ResetPasswordAsync(user, model.Token, model.Password);
        if (result.Succeeded)
        {
            _logger.LogInformation("Password reset completed for {User}", user.Id);
            return RedirectToAction(nameof(ResetPasswordDone));
        }

        foreach (var e in result.Errors)
        {
            // Identity returns "InvalidToken" for both expired and already-used links;
            // say that in words a person can act on.
            ModelState.AddModelError(string.Empty,
                e.Code == "InvalidToken"
                    ? "This link has expired or was already used. Request a new one."
                    : e.Description);
        }
        return View(model);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ResetPasswordDone() => View();

    [HttpGet]
    public IActionResult Error()
    {
        return View();
    }
}
