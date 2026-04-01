using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

public class HomeController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPowerBIService _powerBIService;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IPowerBIService powerBIService,
        ILogger<HomeController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _powerBIService = powerBIService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("RCMDashboard");
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
            return RedirectToAction("RCMDashboard");
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
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
