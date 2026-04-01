using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;

namespace Analytika.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IDhaPortalService _dha;
    private readonly IRhaPortalService _rha;

    private static readonly string[] DashboardTabs = { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
    private static readonly string[] ReportTypes = { "ClaimSummary", "ClaimActivity", "RemittanceActivity", "ClaimReceiver", "ClaimClinician", "FinanceTAT", "DenialReport", "ClaimLifeCycle", "SubmissionXML" };

    public AdminController(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
        IDhaPortalService dha, IRhaPortalService rha)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _dha = dha;
        _rha = rha;
    }

    // ─── Users ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Users(string filter = "All")
    {
        var query = _userManager.Users.Include(u => u.UserFacilities).ThenInclude(uf => uf.Facility).AsQueryable();

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
        var creds = await _db.PortalCredentials.Include(c => c.Facility).OrderBy(c => c.Portal).ThenBy(c => c.Facility!.Name).ToListAsync();
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

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
            .Where(c => c.IsActive).ToListAsync();

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
            .OrderBy(c => c.Portal).ThenBy(c => c.Facility!.Name).ToListAsync();

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
        int added = 0, updated = 0, skipped = 0;

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

            var facility = facilities.FirstOrDefault(f => f.Name.Equals(facName, StringComparison.OrdinalIgnoreCase));
            if (facility == null) { skipped++; continue; }

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
        TempData["Success"] = $"Import complete — {added} added, {updated} updated, {skipped} skipped.";
        return RedirectToAction(nameof(Credentials));
    }

    // ─── Export / Import — Users ──────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> ExportUsers()
    {
        var users = await _userManager.Users
            .Include(u => u.UserFacilities).ThenInclude(uf => uf.Facility)
            .OrderBy(u => u.Email).ToListAsync();

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
                var result = await _userManager.CreateAsync(newUser, "Temp@1234!"); // temp password
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

        TempData["Success"] = $"Import complete — {added} added (temp password: Temp@1234!), {updated} updated, {skipped} skipped.";
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
