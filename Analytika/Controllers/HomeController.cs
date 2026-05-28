using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

public class HomeController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDashboardService _dashboard;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDashboardService dashboard,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dashboard = dashboard;
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
