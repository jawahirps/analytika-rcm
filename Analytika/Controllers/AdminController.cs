using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace Analytika.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IDhaPortalService _dha;
    private readonly IRhaPortalService _rha;
    private readonly IPowerBIService _powerBi;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;

    private static readonly string[] DashboardTabs = { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
    private static readonly string[] ReportTypes = { "ClaimSummary", "ClaimActivity", "RemittanceActivity", "ClaimReceiver", "ClaimClinician", "FinanceTAT", "DenialReport", "ClaimLifeCycle", "SubmissionXML" };

    public AdminController(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
        IDhaPortalService dha, IRhaPortalService rha, IPowerBIService powerBi, IEmailService email, IConfiguration configuration)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _dha = dha;
        _rha = rha;
        _powerBi = powerBi;
        _email = email;
        _configuration = configuration;
    }

    // ─── Users ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Users(string filter = "All")
    {
        var query = _userManager.Users.Include(u => u.UserFacilities).ThenInclude(uf => uf.Facility).AsNoTracking().AsQueryable();

        if (filter == "Global") query = query.Where(u => u.UserType == "Global");
        else if (filter == "Facility") query = query.Where(u => u.UserType == "Facility");
        else if (filter == "Active") query = query.Where(u => u.IsActive);
        else if (filter == "Inactive") query = query.Where(u => !u.IsActive);

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        var rows = new List<UserRowViewModel>();

        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            rows.Add(new UserRowViewModel
            {
                Id = u.Id,
                FullName = u.FullName ?? u.Email ?? "",
                Email = u.Email ?? "",
                UserType = u.UserType,
                Role = roles.FirstOrDefault() ?? "—",
                Facilities = u.UserFacilities.Select(uf => uf.Facility?.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList(),
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt
            });
        }

        return View(new UserListViewModel { Users = rows, TotalUsers = rows.Count, Filter = filter });
    }

    [HttpGet]
    public async Task<IActionResult> CreateUser()
    {
        return View(await BuildCreateVmAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreateUserViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            var fresh = await BuildCreateVmAsync();
            vm.AvailableRoles = fresh.AvailableRoles;
            vm.AvailableFacilities = fresh.AvailableFacilities;
            vm.AllDashboardTabs = fresh.AllDashboardTabs;
            vm.AllReportTypes = fresh.AllReportTypes;
            return View(vm);
        }

        var user = new ApplicationUser
        {
            UserName = vm.Email,
            Email = vm.Email,
            FullName = vm.FullName,
            UserType = vm.UserType,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, vm.Password);
        if (!result.Succeeded)
        {
            foreach (var err in result.Errors) ModelState.AddModelError("", err.Description);
            var fresh = await BuildCreateVmAsync();
            vm.AvailableRoles = fresh.AvailableRoles;
            vm.AvailableFacilities = fresh.AvailableFacilities;
            vm.AllDashboardTabs = fresh.AllDashboardTabs;
            vm.AllReportTypes = fresh.AllReportTypes;
            return View(vm);
        }

        await _userManager.AddToRoleAsync(user, vm.Role);

        // Facility assignments
        foreach (var fid in vm.SelectedFacilityIds)
            _db.UserFacilities.Add(new UserFacility { UserId = user.Id, FacilityId = fid });

        // Report/Dashboard access
        foreach (var tab in vm.SelectedDashboards)
            _db.UserReportAccesses.Add(new UserReportAccess { UserId = user.Id, ResourceType = "Dashboard", ResourceKey = tab });
        foreach (var rpt in vm.SelectedReports)
            _db.UserReportAccesses.Add(new UserReportAccess { UserId = user.Id, ResourceType = "Report", ResourceKey = rpt });

        await _db.SaveChangesAsync();
        TempData["Success"] = $"User '{vm.Email}' created successfully.";
        return RedirectToAction(nameof(Users));
    }

    [HttpGet]
    public async Task<IActionResult> EditUser(string id)
    {
        var user = await _userManager.Users
            .Include(u => u.UserFacilities)
            .Include(u => u.ReportAccesses)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();

        var roles = await _userManager.GetRolesAsync(user);
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();
        var allRoles = await _roleManager.Roles.Select(r => r.Name!).ToListAsync();

        return View(new EditUserViewModel
        {
            Id = user.Id,
            FullName = user.FullName ?? "",
            Email = user.Email ?? "",
            Role = roles.FirstOrDefault() ?? "Analyst",
            UserType = user.UserType,
            IsActive = user.IsActive,
            SelectedFacilityIds = user.UserFacilities.Select(uf => uf.FacilityId).ToList(),
            AvailableRoles = allRoles.Select(r => new SelectListItem(r, r)).ToList(),
            AvailableFacilities = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList(),
            AllDashboardTabs = DashboardTabs.ToList(),
            AllReportTypes = ReportTypes.ToList(),
            SelectedDashboards = user.ReportAccesses.Where(a => a.ResourceType == "Dashboard").Select(a => a.ResourceKey).ToList(),
            SelectedReports = user.ReportAccesses.Where(a => a.ResourceType == "Report").Select(a => a.ResourceKey).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(EditUserViewModel vm)
    {
        var user = await _userManager.Users
            .Include(u => u.UserFacilities)
            .Include(u => u.ReportAccesses)
            .FirstOrDefaultAsync(u => u.Id == vm.Id);
        if (user == null) return NotFound();

        user.FullName = vm.FullName;
        user.UserType = vm.UserType;
        user.IsActive = vm.IsActive;
        await _userManager.UpdateAsync(user);

        // Update role
        var currentRoles = await _userManager.GetRolesAsync(user);
        await _userManager.RemoveFromRolesAsync(user, currentRoles);
        await _userManager.AddToRoleAsync(user, vm.Role);

        // Update facilities
        _db.UserFacilities.RemoveRange(user.UserFacilities);
        foreach (var fid in vm.SelectedFacilityIds)
            _db.UserFacilities.Add(new UserFacility { UserId = user.Id, FacilityId = fid });

        // Update report access
        _db.UserReportAccesses.RemoveRange(user.ReportAccesses);
        foreach (var tab in vm.SelectedDashboards)
            _db.UserReportAccesses.Add(new UserReportAccess { UserId = user.Id, ResourceType = "Dashboard", ResourceKey = tab });
        foreach (var rpt in vm.SelectedReports)
            _db.UserReportAccesses.Add(new UserReportAccess { UserId = user.Id, ResourceType = "Report", ResourceKey = rpt });

        await _db.SaveChangesAsync();
        TempData["Success"] = "User updated successfully.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUser(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);
        TempData["Success"] = $"User {(user.IsActive ? "activated" : "deactivated")}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        TempData[result.Succeeded ? "Success" : "Error"] = result.Succeeded
            ? "Password reset successfully."
            : string.Join("; ", result.Errors.Select(e => e.Description));
        return RedirectToAction(nameof(Users));
    }

    // ─── Credentials ─────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Credentials()
    {
        var creds = await _db.PortalCredentials.Include(c => c.Facility).AsNoTracking().OrderBy(c => c.Portal).ThenBy(c => c.Facility!.Name).ToListAsync();
        var facilities = await _db.Facilities.Where(f => f.IsActive).AsNoTracking().ToListAsync();

        return View(new CredentialListViewModel
        {
            Credentials = creds.Select(c => new PortalCredentialViewModel
            {
                Id = c.Id,
                Portal = c.Portal,
                FacilityId = c.FacilityId,
                FacilityName = c.Facility?.Name ?? "",
                CredentialName = c.CredentialName,
                Username = c.Username,
                Password = "", // never expose
                ApiBaseUrl = c.ApiBaseUrl,
                LicenseCode = c.LicenseCode,
                IsActive = c.IsActive,
                UpdatedAt = c.UpdatedAt
            }).ToList(),
            Facilities = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCredential(PortalCredentialViewModel vm)
    {
        // Auto-create facility if user typed a brand-new name
        if (vm.FacilityId == 0 && !string.IsNullOrWhiteSpace(vm.NewFacilityName))
        {
            var facName = vm.NewFacilityName.Trim();
            var existing = await _db.Facilities.FirstOrDefaultAsync(f => f.Name == facName);
            if (existing != null)
            {
                vm.FacilityId = existing.Id;
            }
            else
            {
                var newFac = new Facility { Name = facName, IsActive = true };
                _db.Facilities.Add(newFac);
                await _db.SaveChangesAsync();
                vm.FacilityId = newFac.Id;
            }
        }

        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(vm.Password));
        if (vm.Id == 0)
        {
            _db.PortalCredentials.Add(new PortalCredential
            {
                Portal = vm.Portal,
                FacilityId = vm.FacilityId,
                CredentialName = string.IsNullOrWhiteSpace(vm.CredentialName) ? null : vm.CredentialName.Trim(),
                Username = vm.Username,
                PasswordEncrypted = enc,
                ApiBaseUrl = vm.ApiBaseUrl,
                LicenseCode = vm.LicenseCode,
                IsActive = vm.IsActive
            });
        }
        else
        {
            var cred = await _db.PortalCredentials.FindAsync(vm.Id);
            if (cred != null)
            {
                cred.Portal = vm.Portal;
                cred.FacilityId = vm.FacilityId;
                cred.CredentialName = string.IsNullOrWhiteSpace(vm.CredentialName) ? null : vm.CredentialName.Trim();
                cred.Username = vm.Username;
                if (!string.IsNullOrWhiteSpace(vm.Password)) cred.PasswordEncrypted = enc;
                cred.ApiBaseUrl = vm.ApiBaseUrl;
                cred.LicenseCode = vm.LicenseCode;
                cred.IsActive = vm.IsActive;
                cred.UpdatedAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "Credential saved.";
        return RedirectToAction(nameof(Credentials));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCredential(int id)
    {
        var cred = await _db.PortalCredentials.FindAsync(id);
        if (cred != null) { _db.PortalCredentials.Remove(cred); await _db.SaveChangesAsync(); }
        TempData["Success"] = "Credential deleted.";
        return RedirectToAction(nameof(Credentials));
    }

    // ─── Credential Test ─────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> TestCredential(int id)
    {
        var cred = await _db.PortalCredentials.Include(c => c.Facility).FirstOrDefaultAsync(c => c.Id == id);
        if (cred == null)
            return Json(new { ok = false, message = "Credential not found.", latencyMs = 0 });

        string pwd;
        try { pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted)); }
        catch { return Json(new { ok = false, message = "Stored password is corrupted.", latencyMs = 0 }); }

        var sw = Stopwatch.StartNew();

        if (cred.Portal == "DHA")
        {
            try
            {
                var (count, _, error) = await _dha.GetNewTransactionsAsync(cred.Username, pwd);
                sw.Stop();
                if (error != null && error.Contains("Connection failed"))
                    return Json(new { ok = false, message = $"Network error — cannot reach DHA portal. ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
                if (error != null && (error.Contains("nvalid") || error.Contains("uthoriz") || error.Contains("401") || error.Contains("403")))
                    return Json(new { ok = false, message = $"Authentication failed — check username/password. ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
                // Any response (even 0 records) means credentials are valid
                return Json(new { ok = true, message = $"DHA connected successfully. {count} new transaction(s) available. ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Json(new { ok = false, message = $"Exception: {ex.Message} ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
            }
        }
        else // RHA
        {
            var baseUrl = cred.ApiBaseUrl ?? "https://tmbapi.riayati.ae:8083";
            try
            {
                var (token, error) = await _rha.AuthenticateAsync(cred.Username, pwd, baseUrl);
                sw.Stop();
                if (token != null)
                    return Json(new { ok = true, message = $"RHA authenticated successfully — Bearer token received. ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
                return Json(new { ok = false, message = $"RHA auth failed — {error} ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Json(new { ok = false, message = $"Exception: {ex.Message} ({sw.ElapsedMilliseconds} ms)", latencyMs = sw.ElapsedMilliseconds });
            }
        }
    }

    [HttpGet]
    public async Task<IActionResult> TestAllCredentials()
    {
        var creds = await _db.PortalCredentials.Include(c => c.Facility)
            .Where(c => c.IsActive).AsNoTracking().ToListAsync();

        var results = new List<object>();
        foreach (var cred in creds)
        {
            string pwd;
            try { pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted)); }
            catch { results.Add(new { id = cred.Id, portal = cred.Portal, facility = cred.Facility?.Name, ok = false, message = "Corrupted password", latencyMs = 0 }); continue; }

            var sw = Stopwatch.StartNew();
            bool ok = false;
            string msg;
            if (cred.Portal == "DHA")
            {
                var (count, _, error) = await _dha.GetNewTransactionsAsync(cred.Username, pwd);
                sw.Stop();
                ok = error == null || (!error.Contains("Connection failed") && !error.Contains("nvalid") && !error.Contains("401") && !error.Contains("403"));
                msg = ok ? $"Connected — {count} new transaction(s). ({sw.ElapsedMilliseconds} ms)" : $"Failed — {error} ({sw.ElapsedMilliseconds} ms)";
            }
            else
            {
                var (token, error) = await _rha.AuthenticateAsync(cred.Username, pwd, cred.ApiBaseUrl ?? "https://tmbapi.riayati.ae:8083");
                sw.Stop();
                ok = token != null;
                msg = ok ? $"Authenticated — token received. ({sw.ElapsedMilliseconds} ms)" : $"Failed — {error} ({sw.ElapsedMilliseconds} ms)";
            }
            results.Add(new { id = cred.Id, portal = cred.Portal, facility = cred.Facility?.Name, ok, message = msg, latencyMs = sw.ElapsedMilliseconds });
        }
        return Json(results);
    }

    // ─── Export / Import — Portal Credentials ────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> ExportCredentials()
    {
        var creds = await _db.PortalCredentials.Include(c => c.Facility)
            .AsNoTracking().OrderBy(c => c.Portal).ThenBy(c => c.Facility!.Name).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Portal,FacilityName,CredentialName,Username,Password,ApiBaseUrl,LicenseCode,IsActive");
        foreach (var c in creds)
        {
            string pwd;
            try { pwd = Encoding.UTF8.GetString(Convert.FromBase64String(c.PasswordEncrypted)); }
            catch { pwd = ""; }
            sb.AppendLine($"{Csv(c.Portal)},{Csv(c.Facility?.Name ?? "")},{Csv(c.CredentialName ?? "")},{Csv(c.Username)},{Csv(pwd)},{Csv(c.ApiBaseUrl ?? "")},{Csv(c.LicenseCode ?? "")},{c.IsActive}");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"portal_credentials_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCredentials(IFormFile file)
    {
        if (file == null || file.Length == 0) { TempData["Error"] = "No file selected."; return RedirectToAction(nameof(Credentials)); }

        var facilities = await _db.Facilities.ToListAsync();
        int added = 0, updated = 0, skipped = 0, facAdded = 0;

        using var reader = new System.IO.StreamReader(file.OpenReadStream());
        var header = await reader.ReadLineAsync(); // skip header
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = SplitCsv(line);
            if (cols.Length < 5) { skipped++; continue; }

            var portal   = cols[0].Trim();
            var facName  = cols[1].Trim();
            var credName = cols.Length > 2 ? cols[2].Trim() : "";
            var username = cols.Length > 3 ? cols[3].Trim() : "";
            var password = cols.Length > 4 ? cols[4].Trim() : "";
            var apiUrl   = cols.Length > 5 ? cols[5].Trim() : "";
            var license  = cols.Length > 6 ? cols[6].Trim() : "";
            bool isActive = cols.Length <= 7 || !cols[7].Trim().Equals("False", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(portal) || string.IsNullOrWhiteSpace(username)) { skipped++; continue; }

            // Find facility by name; auto-create if not found
            var facility = facilities.FirstOrDefault(f => f.Name.Equals(facName, StringComparison.OrdinalIgnoreCase));
            if (facility == null)
            {
                if (string.IsNullOrWhiteSpace(facName)) { skipped++; continue; }
                facility = new Facility { Name = facName, IsActive = true };
                _db.Facilities.Add(facility);
                await _db.SaveChangesAsync(); // get the new Id
                facilities.Add(facility);
                facAdded++;
            }

            var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

            var existing = await _db.PortalCredentials
                .FirstOrDefaultAsync(c => c.Portal == portal && c.FacilityId == facility.Id && c.Username == username);

            if (existing == null)
            {
                _db.PortalCredentials.Add(new PortalCredential
                {
                    Portal = portal, FacilityId = facility.Id, CredentialName = credName.Length > 0 ? credName : null,
                    Username = username, PasswordEncrypted = enc, ApiBaseUrl = apiUrl.Length > 0 ? apiUrl : null,
                    LicenseCode = license.Length > 0 ? license : null, IsActive = isActive
                });
                added++;
            }
            else
            {
                existing.CredentialName = credName.Length > 0 ? credName : null;
                if (!string.IsNullOrWhiteSpace(password)) existing.PasswordEncrypted = enc;
                existing.ApiBaseUrl = apiUrl.Length > 0 ? apiUrl : null;
                existing.LicenseCode = license.Length > 0 ? license : null;
                existing.IsActive = isActive;
                existing.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        await _db.SaveChangesAsync();
        var facMsg = facAdded > 0 ? $", {facAdded} new facilit{(facAdded == 1 ? "y" : "ies")} created" : "";
        TempData["Success"] = $"Import complete — {added} added, {updated} updated, {skipped} skipped{facMsg}.";
        return RedirectToAction(nameof(Credentials));
    }

    // ─── Export / Import — Users ──────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> ExportUsers()
    {
        var users = await _userManager.Users
            .Include(u => u.UserFacilities).ThenInclude(uf => uf.Facility)
            .AsNoTracking().OrderBy(u => u.Email).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Email,FullName,Department,UserType,Role,IsActive,Facilities");
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            var facNames = u.UserFacilities.Select(uf => uf.Facility?.Name ?? "").Where(n => n.Length > 0);
            sb.AppendLine($"{Csv(u.Email ?? "")},{Csv(u.FullName ?? "")},{Csv(u.Department ?? "")},{Csv(u.UserType)},{Csv(string.Join("|", roles))},{u.IsActive},{Csv(string.Join("|", facNames))}");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"users_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportUsers(IFormFile file)
    {
        if (file == null || file.Length == 0) { TempData["Error"] = "No file selected."; return RedirectToAction(nameof(Users)); }

        var facilities = await _db.Facilities.ToListAsync();
        int added = 0, updated = 0, skipped = 0;

        using var reader = new System.IO.StreamReader(file.OpenReadStream());
        await reader.ReadLineAsync(); // skip header
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = SplitCsv(line);
            if (cols.Length < 4) { skipped++; continue; }

            var email      = cols[0].Trim();
            var fullName   = cols.Length > 1 ? cols[1].Trim() : "";
            var dept       = cols.Length > 2 ? cols[2].Trim() : "";
            var userType   = cols.Length > 3 ? cols[3].Trim() : "Global";
            var roleStr    = cols.Length > 4 ? cols[4].Trim() : "Viewer";
            bool isActive  = cols.Length <= 5 || !cols[5].Trim().Equals("False", StringComparison.OrdinalIgnoreCase);
            var facNames   = cols.Length > 6 ? cols[6].Split('|', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(email)) { skipped++; continue; }

            var existingUser = await _userManager.FindByEmailAsync(email);
            if (existingUser == null)
            {
                var newUser = new ApplicationUser
                {
                    UserName = email, Email = email, FullName = fullName,
                    Department = dept, UserType = userType, IsActive = isActive,
                    EmailConfirmed = true
                };
                // Generate a random temporary password to avoid hardcoding a known weak default
                var tempPassword = $"Tmp!{Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(9)).Replace("=", "").Replace("+", "A").Replace("/", "B")}1";
                var result = await _userManager.CreateAsync(newUser, tempPassword);
                if (!result.Succeeded) { skipped++; continue; }

                if (!string.IsNullOrWhiteSpace(roleStr))
                    foreach (var r in roleStr.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        await _userManager.AddToRoleAsync(newUser, r.Trim());

                foreach (var fn in facNames)
                {
                    var fac = facilities.FirstOrDefault(f => f.Name.Equals(fn.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (fac != null)
                        _db.UserFacilities.Add(new UserFacility { UserId = newUser.Id, FacilityId = fac.Id });
                }
                await _db.SaveChangesAsync();
                added++;
            }
            else
            {
                existingUser.FullName   = fullName;
                existingUser.Department = dept;
                existingUser.UserType   = userType;
                existingUser.IsActive   = isActive;
                await _userManager.UpdateAsync(existingUser);
                updated++;
            }
        }

        TempData["Success"] = $"Import complete — {added} added (use Password Reset to set their passwords), {updated} updated, {skipped} skipped.";
        return RedirectToAction(nameof(Users));
    }

    // ─── CSV helpers ──────────────────────────────────────────────────────────

    private static string Csv(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    // ─── DHPO Coding Sets ─────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> CodingSets()
    {
        var counts = await _db.DhpoCodingSets
            .GroupBy(x => x.Category)
            .Select(g => new { Category = g.Key, Count = g.Count(), Latest = g.Max(x => x.ImportedAt) })
            .ToListAsync();

        ViewBag.FacilityCount  = counts.FirstOrDefault(c => c.Category == "Facility")?.Count ?? 0;
        ViewBag.ClinicianCount = counts.FirstOrDefault(c => c.Category == "Clinician")?.Count ?? 0;
        ViewBag.PayerCount     = counts.FirstOrDefault(c => c.Category == "Payer")?.Count ?? 0;
        ViewBag.FacilityDate   = counts.FirstOrDefault(c => c.Category == "Facility")?.Latest.ToString("dd MMM yyyy HH:mm");
        ViewBag.ClinicianDate  = counts.FirstOrDefault(c => c.Category == "Clinician")?.Latest.ToString("dd MMM yyyy HH:mm");
        ViewBag.PayerDate      = counts.FirstOrDefault(c => c.Category == "Payer")?.Latest.ToString("dd MMM yyyy HH:mm");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCodingSet(IFormFile file, string category)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (!new[] { "Facility", "Clinician", "Payer" }.Contains(category))
            return BadRequest("Invalid category.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
            return BadRequest("Only Excel files (.xlsx / .xls) are supported.");

        try
        {
            using var stream = file.OpenReadStream();
            using var wb     = new XLWorkbook(stream);
            var ws = wb.Worksheet(1);

            // Read header row (row 1) — build column index map
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 20;
            for (int c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(1, c).GetString().Trim();
                if (!string.IsNullOrEmpty(h) && !headers.ContainsKey(h))
                    headers[h] = c;
            }

            // Flexible column matching — try multiple candidate names per field
            static int? Col(Dictionary<string, int> h, params string[] candidates)
            {
                foreach (var name in candidates)
                    if (h.TryGetValue(name, out var idx)) return idx;
                return null;
            }

            int? codeCol = category switch
            {
                "Facility"  => Col(headers, "Facility ID", "FacilityID", "License Number", "LicenseNumber", "Code", "ID"),
                "Clinician" => Col(headers, "Clinician ID", "ClinicianID", "Provider ID", "ProviderID", "License Number", "Code", "ID"),
                "Payer"     => Col(headers, "Payer Code", "PayerCode", "Company Code", "CompanyCode", "Code", "ID", "TPA Code", "InsuranceCode"),
                _           => null
            };

            int? nameCol = Col(headers, "Name", "Full Name", "FullName", "Facility Name", "FacilityName",
                               "Clinician Name", "ClinicianName", "Company Name", "CompanyName", "Payer Name");

            int? subTypeCol = category switch
            {
                "Clinician" => Col(headers, "Specialty", "Speciality", "Type", "Category"),
                "Payer"     => Col(headers, "Type", "Company Type", "CompanyType", "Payer Type"),
                "Facility"  => Col(headers, "Type", "Facility Type", "License Type"),
                _           => null
            };

            if (codeCol == null || nameCol == null)
                return BadRequest($"Cannot find required columns (Code / Name) in the Excel file. " +
                    $"Found columns: {string.Join(", ", headers.Keys)}");

            // Determine which extra columns to capture
            var extraCols = headers
                .Where(h => h.Value != codeCol && h.Value != nameCol && h.Value != subTypeCol)
                .Take(5)  // cap at 5 extra columns
                .ToList();

            // Delete old records for this category then bulk insert
            await _db.Database.ExecuteSqlAsync(
                $"DELETE FROM DhpoCodingSets WHERE Category = {category}");

            var now    = DateTime.UtcNow;
            int added  = 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (int r = 2; r <= lastRow; r++)
            {
                var code = ws.Cell(r, codeCol.Value).GetString().Trim();
                var name = ws.Cell(r, nameCol.Value).GetString().Trim();
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name)) continue;

                string? subType = subTypeCol.HasValue
                    ? ws.Cell(r, subTypeCol.Value).GetString().Trim().NullIfEmpty()
                    : null;

                string? extraJson = null;
                if (extraCols.Count > 0)
                {
                    var extras = new Dictionary<string, string?>();
                    foreach (var (h, ci) in extraCols)
                        extras[h] = ws.Cell(r, ci).GetString().Trim().NullIfEmpty();
                    if (extras.Any(x => x.Value != null))
                        extraJson = JsonSerializer.Serialize(extras);
                }

                _db.DhpoCodingSets.Add(new DhpoCodingSet
                {
                    Category   = category,
                    Code       = code,
                    Name       = name,
                    SubType    = subType,
                    ExtraJson  = extraJson,
                    ImportedAt = now
                });
                added++;

                if (added % 500 == 0)
                    await _db.SaveChangesAsync();
            }
            await _db.SaveChangesAsync();

            TempData["Success"] = $"{category} coding set imported — {added} records loaded.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Import failed: {ex.Message}";
        }

        return RedirectToAction(nameof(CodingSets));
    }

    [HttpGet]
    public async Task<IActionResult> CodingSetSearch(string category, string? q, int page = 1)
    {
        const int pageSize = 50;
        var query = _db.DhpoCodingSets.AsNoTracking().Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => x.Code.Contains(q) || x.Name.Contains(q));

        var total = await query.CountAsync();
        var rows  = await query.OrderBy(x => x.Code)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Json(new { total, page, rows = rows.Select(r => new { r.Code, r.Name, r.SubType }) });
    }

    // ─── Power BI Reports ─────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> PowerBIReports()
    {
        var embeds = await _db.DashboardEmbeds.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        var tenantId     = _configuration["PowerBI:TenantId"] ?? "";
        var clientId     = _configuration["PowerBI:ClientId"] ?? "";
        var clientSecret = _configuration["PowerBI:ClientSecret"] ?? "";
        ViewBag.TenantId     = tenantId.StartsWith("YOUR_") ? "" : tenantId;
        ViewBag.ClientId     = clientId.StartsWith("YOUR_") ? "" : clientId;
        ViewBag.ClientSecret = clientSecret.StartsWith("YOUR_") ? "" : clientSecret;
        return View(embeds);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePowerBIEmbed(int id, string groupId, string reportId, bool isActive)
    {
        var embed = await _db.DashboardEmbeds.FindAsync(id);
        if (embed == null) return NotFound();
        embed.GroupId    = groupId.Trim();
        embed.ReportId   = reportId.Trim();
        embed.EmbedToken = "PENDING";  // force refresh on next load
        embed.TokenExpiry = DateTime.UtcNow;
        embed.IsActive   = isActive;
        if (!string.IsNullOrWhiteSpace(embed.GroupId) && !string.IsNullOrWhiteSpace(embed.ReportId))
            embed.EmbedUrl = $"https://app.powerbi.com/reportEmbed?reportId={embed.ReportId}&groupId={embed.GroupId}";
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestPowerBIConnection()
    {
        var err = await _powerBi.TestConnectionAsync();
        return Json(err == null ? new { ok = true, message = "Connected successfully." } : new { ok = false, message = err });
    }

    // ─── Email (SMTP) Settings ────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> EmailSettings()
    {
        var smtp = await _email.GetSmtpSettingsAsync();
        return View(smtp);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveEmailSettings(string host, int port, bool enableSsl,
        string userName, string password, string fromAddress, string fromName)
    {
        var keys = new Dictionary<string, string?>
        {
            ["Host"]        = host,
            ["Port"]        = port.ToString(),
            ["EnableSsl"]   = enableSsl.ToString(),
            ["UserName"]    = userName,
            ["Password"]    = string.IsNullOrWhiteSpace(password) ? null : password,  // null = keep existing
            ["FromAddress"] = fromAddress,
            ["FromName"]    = fromName
        };

        foreach (var kv in keys)
        {
            if (kv.Value == null) continue;  // skip blank password (keep current)
            var setting = await _db.SystemSettings
                .FirstOrDefaultAsync(s => s.Category == "SMTP" && s.Key == kv.Key);
            if (setting == null)
            {
                _db.SystemSettings.Add(new Models.SystemSetting
                    { Category = "SMTP", Key = kv.Key, Value = kv.Value });
            }
            else
            {
                setting.Value     = kv.Value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "SMTP settings saved.";
        return RedirectToAction(nameof(EmailSettings));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmailSettings(string testTo)
    {
        if (string.IsNullOrWhiteSpace(testTo))
            return Json(new { ok = false, message = "Enter a recipient address." });
        try
        {
            // Create a dummy temp file for the test
            var tmpFile = Path.Combine(Path.GetTempPath(), "test-report.txt");
            await System.IO.File.WriteAllTextAsync(tmpFile, "This is a test email from GhafBI.");
            await _email.SendReportAsync(testTo, "TEST-001", "Connection Test", tmpFile);
            System.IO.File.Delete(tmpFile);
            return Json(new { ok = true, message = $"Test email sent to {testTo}." });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
    }

    // ─── Report Schedules ─────────────────────────────────────────────────────

    private static readonly string[] ReportTypeOptions =
    {
        "ClaimSummary", "ClaimActivity", "RemittanceActivity", "ClaimReceiver",
        "ClaimClinician", "FinanceTAT", "DenialReport", "ClaimLifeCycle"
    };

    private static readonly Dictionary<string, string> CronPresets = new()
    {
        ["Daily 8am"]          = "0 8 * * *",
        ["Weekly Mon 8am"]     = "0 8 * * 1",
        ["Monthly 1st 8am"]    = "0 8 1 * *",
        ["Monthly 15th 8am"]   = "0 8 15 * *",
        ["Quarterly (Jan/Apr/Jul/Oct)"] = "0 8 1 1,4,7,10 *"
    };

    [HttpGet]
    public async Task<IActionResult> ReportSchedules()
    {
        var schedules = await _db.ReportSchedules.AsNoTracking().OrderByDescending(s => s.CreatedAt).ToListAsync();
        var facilities = await _db.Facilities.Where(f => f.IsActive)
            .AsNoTracking().Select(f => new { f.Id, f.Name }).ToListAsync();
        ViewBag.ReportTypes  = ReportTypeOptions;
        ViewBag.CronPresets  = CronPresets;
        ViewBag.Facilities   = facilities;
        return View(schedules);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReportSchedule(
        int id, string name, string reportType, string cronExpression,
        string recipients, string fileFormat, bool isActive,
        [FromForm(Name = "facilityIds")] List<int>? facilityIds)
    {
        var facilityJson = facilityIds?.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(facilityIds) : null;

        if (id == 0)
        {
            var s = new Models.ReportSchedule
            {
                Name = name, ReportType = reportType, CronExpression = cronExpression,
                Recipients = recipients, FileFormat = fileFormat,
                IsActive = isActive, FacilityIdsJson = facilityJson
            };
            _db.ReportSchedules.Add(s);
            await _db.SaveChangesAsync();
            // Register with Hangfire
            if (isActive)
                RegisterHangfireJob(s);
        }
        else
        {
            var s = await _db.ReportSchedules.FindAsync(id);
            if (s == null) return NotFound();
            s.Name = name; s.ReportType = reportType; s.CronExpression = cronExpression;
            s.Recipients = recipients; s.FileFormat = fileFormat;
            s.IsActive = isActive; s.FacilityIdsJson = facilityJson;
            await _db.SaveChangesAsync();
            if (isActive) RegisterHangfireJob(s);
            else Hangfire.RecurringJob.RemoveIfExists($"schedule-{s.Id}");
        }

        return Json(new { ok = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReportSchedule(int id)
    {
        var s = await _db.ReportSchedules.FindAsync(id);
        if (s == null) return NotFound();
        Hangfire.RecurringJob.RemoveIfExists($"schedule-{s.Id}");
        _db.ReportSchedules.Remove(s);
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RunScheduleNow(int id)
    {
        Hangfire.BackgroundJob.Enqueue<PortalSyncService>(svc => svc.RunDailyDhaSyncAsync());
        return Json(new { ok = true, message = "Schedule enqueued for immediate execution." });
    }

    private static void RegisterHangfireJob(Models.ReportSchedule s)
    {
        // Use a no-op placeholder — real report generation wired via PortalSyncService
        Hangfire.RecurringJob.AddOrUpdate<PortalSyncService>(
            $"schedule-{s.Id}",
            svc => svc.RunDailyDhaSyncAsync(),
            s.CronExpression);
    }

    // ─── Database Backup & Migration ─────────────────────────────────────────

    private string GetDbPath()
    {
        var dataDir = Environment.GetEnvironmentVariable("DB_DIR")
            ?? System.IO.Path.Combine(AppContext.BaseDirectory);
        return System.IO.Path.Combine(dataDir, "analytika.db");
    }

    [HttpGet]
    public IActionResult Database()
    {
        var dbPath = GetDbPath();
        var info   = System.IO.File.Exists(dbPath) ? new System.IO.FileInfo(dbPath) : null;
        ViewBag.DbPath  = dbPath;
        ViewBag.DbSizeMb = info != null ? Math.Round(info.Length / 1_048_576.0, 1) : 0;
        ViewBag.DbModified = info?.LastWriteTimeUtc.ToString("dd MMM yyyy HH:mm UTC");
        ViewBag.PendingExists = System.IO.File.Exists(dbPath + ".pending");
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> ExportDatabase()
    {
        // Flush WAL into the main file so the download is self-contained
        await _db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL)");
        var dbPath = GetDbPath();
        if (!System.IO.File.Exists(dbPath)) return NotFound();
        var bytes = await System.IO.File.ReadAllBytesAsync(dbPath);
        return File(bytes, "application/octet-stream", $"analytika_{DateTime.UtcNow:yyyyMMdd_HHmm}.db");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.Mvc.RequestSizeLimit(3L * 1024 * 1024 * 1024)]
    [Microsoft.AspNetCore.Mvc.RequestFormLimits(MultipartBodyLengthLimit = 3L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> ImportDatabase(IFormFile dbFile)
    {
        if (dbFile == null || dbFile.Length == 0)
            return Json(new { ok = false, message = "No file selected." });

        // Validate SQLite magic header (first 16 bytes)
        var magic = new byte[16];
        using (var peek = dbFile.OpenReadStream())
            await peek.ReadExactlyAsync(magic, 0, 16);

        var expected = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
        if (!magic.AsSpan().SequenceEqual(expected))
            return Json(new { ok = false, message = "File is not a valid SQLite database." });

        var pendingPath = GetDbPath() + ".pending";
        using (var fs = System.IO.File.Create(pendingPath))
            await dbFile.CopyToAsync(fs);

        var sizeMb = Math.Round(dbFile.Length / 1_048_576.0, 1);
        return Json(new { ok = true, message = $"Database uploaded ({sizeMb} MB). Restart the service to apply it." });
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private async Task<CreateUserViewModel> BuildCreateVmAsync()
    {
        var roles = await _roleManager.Roles.Select(r => r.Name!).ToListAsync();
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();
        return new CreateUserViewModel
        {
            AvailableRoles = roles.Select(r => new SelectListItem(r, r)).ToList(),
            AvailableFacilities = facilities.Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToList(),
            AllDashboardTabs = DashboardTabs.ToList(),
            AllReportTypes = ReportTypes.ToList()
        };
    }
}
