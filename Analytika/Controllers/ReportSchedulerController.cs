using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Analytika.Controllers;

[Authorize(Roles = AppRoles.ReportAccess)]
[Route("[controller]/[action]")]
public class ReportSchedulerController : Controller
{
    private readonly AppDbContext _context;
    private readonly IReportService _reportService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public ReportSchedulerController(AppDbContext context, IReportService reportService,
        UserManager<ApplicationUser> userManager, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _context = context;
        _reportService = reportService;
        _userManager = userManager;
        _cache = cache;
    }

    // Returns the single facility ID for Facility-type users, null for Global users.
    private async Task<int?> GetUserFacilityIdAsync()
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser?.UserType != "Facility") return null;
        var uf = await _context.Set<UserFacility>()
            .Where(x => x.UserId == appUser.Id)
            .FirstOrDefaultAsync();
        return uf?.FacilityId;
    }

    private async Task<ReportSchedulerViewModel> BuildViewModelAsync(string reportType, string reportTitle, int page = 1)
    {
        var facilityId = await GetUserFacilityIdAsync();
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, 10, facilityId);

        var facilitiesQuery = _context.Facilities.Where(f => f.IsActive);
        if (facilityId.HasValue)
            facilitiesQuery = facilitiesQuery.Where(f => f.Id == facilityId.Value);

        // Scope filter dropdowns to codes that actually appear in this facility's
        // parsed data. These DISTINCT scans walk ~776k rows for Global users and
        // took minutes per page load — cache the resulting SelectList items for
        // 15 minutes per facility scope. (Receiver/Department filters are hidden
        // for this pass, so their scans are gone entirely.)
        var scopeKey = facilityId?.ToString() ?? "all";
        var (payerItems, clinicianItems) = await _cache.GetOrCreateAsync(
            $"report-filter-options:{scopeKey}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                var parsedScope = _context.XmlParsedRecords.AsNoTracking();
                if (facilityId.HasValue)
                    parsedScope = parsedScope.Where(r => r.FacilityId == facilityId.Value);

                var payerCodes = await parsedScope
                    .Where(r => r.PayerId != null && r.PayerId != "")
                    .Select(r => r.PayerId!).Distinct().ToListAsync();
                var clinicianCodes = await parsedScope
                    .Where(r => r.Clinician != null && r.Clinician != "")
                    .Select(r => r.Clinician!).Distinct().ToListAsync();

                var payers = await _context.Payers
                    .Where(p => p.IsActive && payerCodes.Contains(p.Name))
                    .OrderBy(p => p.Name)
                    .Select(p => new SelectListItem { Value = p.Id.ToString(), Text = p.Name })
                    .ToListAsync();
                var clinicians = await _context.Clinicians
                    .Where(c => c.IsActive && clinicianCodes.Contains(c.Name))
                    .OrderBy(c => c.Name)
                    .Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name })
                    .ToListAsync();
                return (payers, clinicians);
            });

        return new ReportSchedulerViewModel
        {
            ReportType = reportType,
            ReportTitle = reportTitle,
            // Remittance-based reports filter by the remittance (settlement/payment)
            // date; claim reports default to encounter start date.
            SearchCriteria = reportType is "RemittanceActivity" or "DenialReport"
                ? "RemittanceDate" : "EncounterStartDate",
            Facilities = new SelectList(await facilitiesQuery.ToListAsync(), "Id", "Name"),
            Payers    = new SelectList(payerItems, "Value", "Text"),
            Clinicians = new SelectList(clinicianItems, "Value", "Text"),
            RecentReports = reports,
            TotalReports = total,
            CurrentPage = page
        };
    }

    public async Task<IActionResult> ClaimSummaryReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("ClaimSummary", "Claim Summary Report", page));

    public async Task<IActionResult> ClaimActivityReports(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("ClaimActivity", "Claim Activity Report", page));

    public async Task<IActionResult> RemittanceActivityReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("RemittanceActivity", "Remittance Activity Report", page));

    public async Task<IActionResult> ClaimReceiverReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("ClaimReceiver", "Claim Receiver Report", page));

    public async Task<IActionResult> ClaimClinicianReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("ClaimClinician", "Claim Clinician Report", page));

    public async Task<IActionResult> FinanceTATReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("FinanceTAT", "Finance TAT Report", page));

    public async Task<IActionResult> DenialReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("DenialReport", "Denial Query Report", page));

    public async Task<IActionResult> ClaimLifeCycleReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("ClaimLifeCycle", "Claim Life Cycle Report", page));

    public async Task<IActionResult> SubmissionXMLReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("SubmissionXML", "Submission XML File Report", page));

    public async Task<IActionResult> LiveSubmissionReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("LiveSubmission", "Live Submission Report", page));

    [HttpGet("/ReportScheduler/SubmitReport")]
    public IActionResult SubmitReport()
        => RedirectToAction(nameof(ClaimSummaryReport));

    [HttpPost("/ReportScheduler/CreateReport")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReport(ReportSchedulerViewModel model)
    {
        var user = User.Identity?.Name ?? "system";

        // Multi-select is the source of truth; the single *Id fields stay populated
        // with the first selected value for backward-compatible list display.
        static string? JsonIds(IEnumerable<int>? ids)
        {
            var v = (ids ?? Enumerable.Empty<int>()).Where(i => i != 0).Distinct().ToList();
            return v.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(v) : null;
        }
        static string? JsonStrings(IEnumerable<string>? vals)
        {
            var v = (vals ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            return v.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(v) : null;
        }

        var encounterTypes = (model.SelectedEncounterTypes != null && model.SelectedEncounterTypes.Count > 0)
            ? model.SelectedEncounterTypes
            : (string.IsNullOrWhiteSpace(model.EncounterType) ? new List<string>() : new List<string> { model.EncounterType });

        // Authoritative date window — computed server-side from the DateRange label,
        // or parsed from the RAW dd/MM/yyyy form strings for Custom. The culture-bound
        // model.DateFrom/DateTo are never trusted (see ReportDateWindow docs: they
        // silently null out or transpose day/month, which produced empty reports).
        if (!ReportDateWindow.TryResolve(model.DateRange,
                Request.Form["DateFrom"].ToString(), Request.Form["DateTo"].ToString(),
                DateTime.Today, out var dateFrom, out var dateTo))
        {
            TempData["Error"] = "Please select a valid date range (custom dates must be DD/MM/YYYY).";
            return RedirectToAction(GetActionName(model.ReportType));
        }

        var request = new ReportRequest
        {
            ReportType = model.ReportType,
            BranchId = model.SelectedFacilities.FirstOrDefault() == 0 ? null : model.SelectedFacilities.FirstOrDefault(),
            ReceiverId = model.SelectedReceivers.FirstOrDefault() == 0 ? null : model.SelectedReceivers.FirstOrDefault(),
            PayerId = model.SelectedPayers.FirstOrDefault() == 0 ? null : model.SelectedPayers.FirstOrDefault(),
            ClinicianId = model.SelectedClinicians.FirstOrDefault() == 0 ? null : model.SelectedClinicians.FirstOrDefault(),
            DepartmentId = model.SelectedDepartments.FirstOrDefault() == 0 ? null : model.SelectedDepartments.FirstOrDefault(),
            EncounterType = encounterTypes.FirstOrDefault(),

            FacilityIdsJson    = JsonIds(model.SelectedFacilities),
            ReceiverIdsJson    = JsonIds(model.SelectedReceivers),
            PayerIdsJson       = JsonIds(model.SelectedPayers),
            ClinicianIdsJson   = JsonIds(model.SelectedClinicians),
            DepartmentIdsJson  = JsonIds(model.SelectedDepartments),
            EncounterTypesJson = JsonStrings(encounterTypes),

            DateFrom = dateFrom,
            DateTo = dateTo,
            SearchCriteria = model.SearchCriteria,
            Template = model.Template,
            FileFormat = "Excel",   // Excel-only export for this pass
            RequestedBy = user,
            EmailTo = string.IsNullOrWhiteSpace(model.EmailTo) ? null : model.EmailTo.Trim()
        };

        var reportId = await _reportService.QueueReportAsync(request, model.DateRange);
        TempData["Success"] = $"Report {reportId} is now generating in the background.";

        return RedirectToAction(GetActionName(model.ReportType));
    }

    [HttpGet]
    public async Task<IActionResult> GetReports(string reportType, int page = 1, int pageSize = 10)
    {
        var facilityId = await GetUserFacilityIdAsync();
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, pageSize, facilityId);
        return Json(new
        {
            data = reports.Select(r => new
            {
                r.Id,
                r.ReportId,
                Branch = r.Branch?.Name ?? "-",
                Receiver = r.Receiver?.Name ?? "-",
                Payer = r.Payer?.Name ?? "-",
                Clinician = r.Clinician?.Name ?? "-",
                r.Status,
                DateFrom = r.DateFrom.ToString("dd/MM/yyyy"),
                DateTo = r.DateTo.ToString("dd/MM/yyyy"),
                RequestedDate = $"{r.DateFrom:dd/MM/yyyy} - {r.DateTo:dd/MM/yyyy}",
                GeneratedOn = r.GeneratedAt?.ToString("dd/MM/yyyy HH:mm") ?? "-",
                r.FilePath
            }),
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id)
    {
        var report = await _reportService.GetReportByIdAsync(id);
        if (report == null || string.IsNullOrEmpty(report.FilePath))
            return NotFound();

        var filePath = ResolveReportFilePath(report.FilePath);
        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found on server.");

        var fileName = Path.GetFileName(filePath);
        // Excel-only export for this pass.
        const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        return PhysicalFile(filePath, contentType, fileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReport(int id, string reportType)
    {
        var report = await _context.ReportRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (report == null)
        {
            TempData["Error"] = "Report request was not found.";
            return RedirectToAction(GetActionName(reportType));
        }

        var activeReport = ReportGenerationState.Get();
        if (activeReport.IsRunning && activeReport.ReportRequestId == report.Id)
        {
            TempData["Error"] = $"Report {report.ReportId} is still running and cannot be deleted yet.";
            return RedirectToAction(GetActionName(report.ReportType));
        }

        var filePath = ResolveReportFilePath(report.FilePath);
        var reportId = report.ReportId;
        var resolvedReportType = report.ReportType;

        _context.ReportRequests.Remove(report);
        await _context.SaveChangesAsync();
        DeleteReportFile(filePath);

        TempData["Success"] = $"Report {reportId} was deleted.";
        return RedirectToAction(GetActionName(resolvedReportType));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearReports(string reportType)
    {
        if (string.IsNullOrWhiteSpace(reportType))
            reportType = "ClaimSummary";

        var activeReport = ReportGenerationState.Get();
        var query = _context.ReportRequests.Where(r => r.ReportType == reportType);

        if (activeReport.IsRunning)
            query = query.Where(r => r.Id != activeReport.ReportRequestId);

        var reports = await query.ToListAsync();
        var filePaths = reports
            .Select(r => ResolveReportFilePath(r.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _context.ReportRequests.RemoveRange(reports);
        await _context.SaveChangesAsync();

        foreach (var filePath in filePaths)
            DeleteReportFile(filePath);

        TempData["Success"] = reports.Count == 0
            ? "No completed report requests were available to clear."
            : $"Cleared {reports.Count} report request(s).";

        return RedirectToAction(GetActionName(reportType));
    }

    // Reports are stored in a persistent folder next to the DB (survives redeploys);
    // resolve by file name against that folder, with a traversal guard.
    private string? ResolveReportFilePath(string? reportFilePath)
    {
        if (string.IsNullOrWhiteSpace(reportFilePath))
            return null;

        var fileName = Path.GetFileName(reportFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var reportsDir = Path.GetFullPath(Analytika.Services.ReportService.ResolveReportsDirectory(
            _context.Database.GetDbConnection().ConnectionString,
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")));
        var filePath = Path.GetFullPath(Path.Combine(reportsDir, fileName));

        return filePath.StartsWith(reportsDir, StringComparison.OrdinalIgnoreCase) ? filePath : null;
    }

    private static void DeleteReportFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);
        }
        catch
        {
            // The history row is the source of truth; stale files can be cleaned up later.
        }
    }

    private static string GetActionName(string reportType) => reportType switch
    {
        "ClaimSummary" => "ClaimSummaryReport",
        "ClaimActivity" => "ClaimActivityReports",
        "RemittanceActivity" => "RemittanceActivityReport",
        "ClaimReceiver" => "ClaimReceiverReport",
        "ClaimClinician" => "ClaimClinicianReport",
        "FinanceTAT" => "FinanceTATReport",
        "DenialReport" => "DenialReport",
        "ClaimLifeCycle" => "ClaimLifeCycleReport",
        "SubmissionXML" => "SubmissionXMLReport",
        "LiveSubmission" => "LiveSubmissionReport",
        _ => "ClaimSummaryReport"
    };
}
