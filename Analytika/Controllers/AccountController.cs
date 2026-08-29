using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Controllers;

[Authorize]
public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;

    public AccountController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var user = await LoadCurrentUserAsync();
        if (user == null) return Challenge();

        return View(await BuildProfileViewModelAsync(user));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(UserProfileViewModel vm)
    {
        var user = await LoadCurrentUserAsync();
        if (user == null) return Challenge();

        if (!ModelState.IsValid)
            return View(await BuildProfileViewModelAsync(user, vm));

        user.FullName = vm.FullName.Trim();
        user.Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(vm.PhoneNumber) ? null : vm.PhoneNumber.Trim();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return View(await BuildProfileViewModelAsync(user, vm));
        }

        TempData["Success"] = "Profile updated.";
        return RedirectToAction(nameof(Profile));
    }

    private async Task<ApplicationUser?> LoadCurrentUserAsync()
    {
        var userId = _userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId)) return null;

        return await _userManager.Users
            .Include(u => u.UserFacilities).ThenInclude(uf => uf.Facility)
            .Include(u => u.ReportAccesses)
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    private async Task<UserProfileViewModel> BuildProfileViewModelAsync(ApplicationUser user, UserProfileViewModel? posted = null)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return new UserProfileViewModel
        {
            FullName = posted?.FullName ?? user.FullName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            Department = posted?.Department ?? user.Department,
            PhoneNumber = posted?.PhoneNumber ?? user.PhoneNumber,
            UserType = user.UserType,
            IsActive = user.IsActive,
            Roles = roles.OrderBy(r => r).ToList(),
            Facilities = user.UserFacilities
                .Where(uf => uf.Facility != null)
                .Select(uf => uf.Facility!.Name)
                .OrderBy(name => name)
                .ToList(),
            DashboardAccess = user.ReportAccesses
                .Where(a => a.ResourceType == "Dashboard")
                .Select(a => a.ResourceKey)
                .OrderBy(key => key)
                .ToList(),
            ReportAccess = user.ReportAccesses
                .Where(a => a.ResourceType == "Report")
                .Select(a => a.ResourceKey)
                .OrderBy(key => key)
                .ToList()
        };
    }
}
