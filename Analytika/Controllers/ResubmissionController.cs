using Analytika.Models;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Analytika.Controllers;

[Authorize]
public class ResubmissionController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RemittanceParserService _parser;

    public ResubmissionController(AppDbContext db, UserManager<ApplicationUser> userManager,
        RemittanceParserService parser)
    {
        _db = db;
        _userManager = userManager;
        _parser = parser;
    }

    // ─── Dashboard ────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index(string? status, int? facilityId, string? priority,
        string? assignee, string? search, int page = 1)
    {
        const int pageSize = 30;

        var userId   = _userManager.GetUserId(User);
        var isAdmin  = User.IsInRole("Admin") || User.IsInRole("FacilityAdmin") || User.IsInRole("Analyst");

        var query = _db.RemittanceClaims
            .Include(rc => rc.Facility)
            .Include(rc => rc.Task)
                .ThenInclude(t => t!.AssignedTo)
            .AsNoTracking()
            .AsQueryable();

        // Non-admin coders see only their assigned tasks
        if (!isAdmin)
            query = query.Where(rc => rc.Task != null && rc.Task.AssignedToUserId == userId);

        if (facilityId.HasValue)  query = query.Where(rc => rc.FacilityId == facilityId.Value);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status == "Unassigned")
                query = query.Where(rc => rc.Task == null || rc.Task.Status == ResubmissionStatus.Unassigned);
            else
                query = query.Where(rc => rc.Task != null && rc.Task.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(priority))
            query = query.Where(rc => rc.Task != null && rc.Task.Priority == priority);
        if (!string.IsNullOrWhiteSpace(assignee))
            query = query.Where(rc => rc.Task != null && rc.Task.AssignedToUserId == assignee);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(rc => rc.ClaimId.Contains(search) || rc.PayerClaimId!.Contains(search)
                || rc.Comments!.Contains(search));

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(rc => rc.Task == null ? 1 : 0) // unassigned first
            .ThenByDescending(rc => (double)rc.OriginalAmount - (double)rc.PaidAmount)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        // Summary counts
        var all = _db.RemittanceClaims.Include(rc => rc.Task).AsNoTracking();
        if (!isAdmin) all = all.Where(rc => rc.Task != null && rc.Task.AssignedToUserId == userId);

        ViewBag.TotalClaims    = await all.CountAsync();
        ViewBag.Unassigned     = await all.CountAsync(rc => rc.Task == null || rc.Task.Status == ResubmissionStatus.Unassigned);
        ViewBag.InProgress     = await all.CountAsync(rc => rc.Task != null && (rc.Task.Status == ResubmissionStatus.InReview || rc.Task.Status == ResubmissionStatus.Assigned));
        ViewBag.Resubmitted    = await all.CountAsync(rc => rc.Task != null && rc.Task.Status == ResubmissionStatus.Resubmitted);
        ViewBag.Closed         = await all.CountAsync(rc => rc.Task != null && (rc.Task.Status == ResubmissionStatus.Closed || rc.Task.Status == ResubmissionStatus.Rejected));
        ViewBag.TotalDenied    = await all.SumAsync(rc => (double)rc.OriginalAmount - (double)rc.PaidAmount);
        ViewBag.TotalRecovered = await all.Where(rc => rc.Task != null && rc.Task.Status == ResubmissionStatus.Resubmitted)
                                          .SumAsync(rc => (double)rc.OriginalAmount - (double)rc.PaidAmount);

        ViewBag.Facilities = await _db.Facilities.Where(f => f.IsActive).AsNoTracking().ToListAsync();
        ViewBag.Coders     = isAdmin ? await _userManager.Users.Where(u => u.IsActive).AsNoTracking().ToListAsync() : new List<ApplicationUser>();
        ViewBag.Statuses   = ResubmissionStatus.All;
        ViewBag.Priorities = ResubmissionPriority.All;
        ViewBag.IsAdmin    = isAdmin;

        // filters for view
        ViewBag.FilterStatus    = status;
        ViewBag.FilterFacility  = facilityId;
        ViewBag.FilterPriority  = priority;
        ViewBag.FilterAssignee  = assignee;
        ViewBag.FilterSearch    = search;
        ViewBag.Page            = page;
        ViewBag.PageSize        = pageSize;
        ViewBag.TotalPages      = (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.TotalFiltered   = total;

        // Unparse count
        var parsedTxIds = await _db.RemittanceClaims.Select(rc => rc.RemittanceTransactionId).ToHashSetAsync();
        ViewBag.UnparsedCount = await _db.PortalTransactions
            .CountAsync(pt => pt.Type == "Remittance" && pt.FileDownloaded && !parsedTxIds.Contains(pt.Id));

        return View(items);
    }

    // ─── Parse remittances ────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,FacilityAdmin,Analyst")]
    public async Task<IActionResult> Parse(int? facilityId)
    {
        var (parsed, skipped, errors) = await _parser.ParsePendingAsync(facilityId);
        TempData["Success"] = $"Parsed {parsed} remittance file(s). Skipped: {skipped}. Errors: {errors}.";
        return RedirectToAction(nameof(Index));
    }

    // ─── Claim detail ─────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Claim(int id)
    {
        var userId  = _userManager.GetUserId(User);
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("FacilityAdmin") || User.IsInRole("Analyst");

        var claim = await _db.RemittanceClaims
            .Include(rc => rc.Facility)
            .Include(rc => rc.RemittanceTransaction)
            .Include(rc => rc.Task)
                .ThenInclude(t => t!.AssignedTo)
            .Include(rc => rc.Task)
                .ThenInclude(t => t!.AssignedBy)
            .AsNoTracking()
            .FirstOrDefaultAsync(rc => rc.Id == id);

        if (claim == null) return NotFound();
        if (!isAdmin && (claim.Task == null || claim.Task.AssignedToUserId != userId))
            return Forbid();

        ViewBag.IsAdmin    = isAdmin;
        ViewBag.Coders     = isAdmin ? await _userManager.Users.Where(u => u.IsActive).AsNoTracking().ToListAsync() : new List<ApplicationUser>();
        ViewBag.Statuses   = ResubmissionStatus.All;
        ViewBag.Priorities = ResubmissionPriority.All;
        ViewBag.DenialCodes = claim.DenialCodesJson != null
            ? JsonSerializer.Deserialize<List<string>>(claim.DenialCodesJson) ?? new()
            : new List<string>();

        return View(claim);
    }

    // ─── Assign / re-assign ───────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,FacilityAdmin,Analyst")]
    public async Task<IActionResult> Assign(int claimId, string assignToUserId, string priority,
        string? dueDate, string? notes)
    {
        var claim = await _db.RemittanceClaims.Include(rc => rc.Task).FirstOrDefaultAsync(rc => rc.Id == claimId);
        if (claim == null) return NotFound();

        var byUserId = _userManager.GetUserId(User);
        var due      = string.IsNullOrWhiteSpace(dueDate) ? (DateTime?)null : DateTime.Parse(dueDate);

        if (claim.Task == null)
        {
            claim.Task = new ResubmissionTask
            {
                RemittanceClaimId = claim.Id,
                AssignedToUserId  = assignToUserId,
                AssignedByUserId  = byUserId,
                AssignedAt        = DateTime.UtcNow,
                DueDate           = due,
                Priority          = priority,
                Status            = ResubmissionStatus.Assigned,
                Notes             = notes
            };
            _db.ResubmissionTasks.Add(claim.Task);
        }
        else
        {
            claim.Task.AssignedToUserId = assignToUserId;
            claim.Task.AssignedByUserId = byUserId;
            claim.Task.AssignedAt       = DateTime.UtcNow;
            claim.Task.DueDate          = due;
            claim.Task.Priority         = priority;
            claim.Task.Status           = ResubmissionStatus.Assigned;
            if (!string.IsNullOrWhiteSpace(notes)) claim.Task.Notes = notes;
            claim.Task.UpdatedAt        = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    // ─── Bulk assign ──────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,FacilityAdmin,Analyst")]
    public async Task<IActionResult> BulkAssign([FromForm] List<int> claimIds, string assignToUserId,
        string priority, string? dueDate)
    {
        if (claimIds.Count == 0) return Json(new { ok = false, message = "No claims selected." });
        var byUserId = _userManager.GetUserId(User);
        var due      = string.IsNullOrWhiteSpace(dueDate) ? (DateTime?)null : DateTime.Parse(dueDate);

        var claims = await _db.RemittanceClaims.Include(rc => rc.Task)
            .Where(rc => claimIds.Contains(rc.Id)).ToListAsync();

        foreach (var claim in claims)
        {
            if (claim.Task == null)
            {
                _db.ResubmissionTasks.Add(new ResubmissionTask
                {
                    RemittanceClaimId = claim.Id,
                    AssignedToUserId  = assignToUserId,
                    AssignedByUserId  = byUserId,
                    AssignedAt        = DateTime.UtcNow,
                    DueDate           = due,
                    Priority          = priority,
                    Status            = ResubmissionStatus.Assigned
                });
            }
            else
            {
                claim.Task.AssignedToUserId = assignToUserId;
                claim.Task.Priority         = priority;
                claim.Task.DueDate          = due;
                claim.Task.Status           = ResubmissionStatus.Assigned;
                claim.Task.UpdatedAt        = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();
        return Json(new { ok = true, message = $"{claims.Count} claim(s) assigned." });
    }

    // ─── Update status (coder action) ────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(int taskId, string status, string? actionTaken, string? notes)
    {
        var userId  = _userManager.GetUserId(User);
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("FacilityAdmin") || User.IsInRole("Analyst");

        var task = await _db.ResubmissionTasks.FindAsync(taskId);
        if (task == null) return NotFound();
        if (!isAdmin && task.AssignedToUserId != userId) return Forbid();

        task.Status    = status;
        task.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(actionTaken)) task.ActionTaken = actionTaken;
        if (!string.IsNullOrWhiteSpace(notes))       task.Notes       = notes;

        if (status == ResubmissionStatus.InReview)     task.StartedAt      ??= DateTime.UtcNow;
        if (status == ResubmissionStatus.Resubmitted)  task.ResubmittedAt    = DateTime.UtcNow;
        if (status is ResubmissionStatus.Closed or ResubmissionStatus.Rejected)
            task.ClosedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    // ─── Stats for dashboard cards (AJAX) ─────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        var userId  = _userManager.GetUserId(User);
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("FacilityAdmin") || User.IsInRole("Analyst");

        var q = _db.RemittanceClaims.Include(rc => rc.Task).AsNoTracking();
        if (!isAdmin) q = q.Where(rc => rc.Task != null && rc.Task.AssignedToUserId == userId);

        var byFacility = await _db.RemittanceClaims
            .Include(rc => rc.Facility).Include(rc => rc.Task)
            .Where(rc => isAdmin || (rc.Task != null && rc.Task.AssignedToUserId == userId))
            .AsNoTracking()
            .GroupBy(rc => rc.Facility!.Name)
            .Select(g => new { Facility = g.Key, Count = g.Count(), Denied = g.Sum(c => (double)c.OriginalAmount - (double)c.PaidAmount) })
            .OrderByDescending(x => x.Denied)
            .ToListAsync();

        return Json(new { byFacility });
    }
}
