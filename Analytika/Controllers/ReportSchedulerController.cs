using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Controllers;

[Authorize(Roles = AppRoles.ReportAccess)]
[Route("[controller]/[action]")]
public class ReportSchedulerController : Controller
{
    private readonly AppDbContext _context;
    private readonly IReportService _reportService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _env;

    public ReportSchedulerController(AppDbContext context, IReportService reportService, UserManager<ApplicationUser> userManager, IWebHostEnvironment env)
    {
        _context = context;
        _reportService = reportService;
        _userManager = userManager;
        _env = env;
    }

    // Returns all facility IDs for Facility-type users, null for global users.
    // Returns an empty list (not null) when a Facility user has no assignments — callers must treat this as "no access".
    private async Task<List<int>?> GetUserFacilityIdsAsync()
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser?.UserType != "Facility") return null;
        return await _context.Set<UserFacility>()
            .Where(x => x.UserId == appUser.Id)
            .Select(x => x.FacilityId)
            .ToListAsync();
    }

    private async Task<ReportSchedulerViewModel> BuildViewModelAsync(string reportType, string reportTitle, int page = 1)
    {
        var facilityIds = await GetUserFacilityIdsAsync();
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, 10, facilityIds);

        var facilitiesQuery = _context.Facilities.Where(f => f.IsActive);
        if (facilityIds != null)
            facilitiesQuery = facilityIds.Count > 0
                ? facilitiesQuery.Where(f => facilityIds.Contains(f.Id))
                : facilitiesQuery.Where(_ => false);

        // Global users can use the maintained lookup tables directly. Scanning
        // distinct values from the full parsed-record ledger makes every report
        // page proportional to the (very large) claims database.
        List<string>? payerCodes = null;
        List<string>? receiverCodes = null;
        List<string>? clinicianCodes = null;
        if (facilityIds != null)
        {
            var parsedScope = _context.XmlParsedRecords.AsNoTracking();
            parsedScope = facilityIds.Count > 0
                ? parsedScope.Where(record => facilityIds.Contains(record.FacilityId))
                : parsedScope.Where(_ => false);
            payerCodes = await parsedScope.Where(record => record.PayerId != null && record.PayerId != "")
                .Select(record => record.PayerId!).Distinct().ToListAsync();
            receiverCodes = await parsedScope.Where(record => record.ReceiverId != null && record.ReceiverId != "")
                .Select(record => record.ReceiverId!).Distinct().ToListAsync();
            clinicianCodes = await parsedScope.Where(record => record.Clinician != null && record.Clinician != "")
                .Select(record => record.Clinician!).Distinct().ToListAsync();
        }

        var payersQuery = _context.Payers.Where(item => item.IsActive);
        var receiversQuery = _context.Receivers.Where(item => item.IsActive);
        var cliniciansQuery = _context.Clinicians.Where(item => item.IsActive);
        if (payerCodes != null) payersQuery = payersQuery.Where(item => payerCodes.Contains(item.Name));
        if (receiverCodes != null) receiversQuery = receiversQuery.Where(item => receiverCodes.Contains(item.Name));
        if (clinicianCodes != null) cliniciansQuery = cliniciansQuery.Where(item => clinicianCodes.Contains(item.Name));

        return new ReportSchedulerViewModel
        {
            ReportType = reportType,
            ReportTitle = reportTitle,
            SearchCriteria = "EncounterStartDate",
            Facilities = new SelectList(await facilitiesQuery.ToListAsync(), "Id", "Name"),
            Payers = new SelectList(await payersQuery.OrderBy(item => item.Name).ToListAsync(), "Id", "Name"),
            Receivers = new SelectList(await receiversQuery.OrderBy(item => item.Name).ToListAsync(), "Id", "Name"),
            Clinicians = new SelectList(await cliniciansQuery.OrderBy(item => item.Name).ToListAsync(), "Id", "Name"),
            Departments = new SelectList(await _context.Departments.Where(d => d.IsActive).ToListAsync(), "Id", "Name"),
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

    public async Task<IActionResult> AuditFlagsReport(int page = 1)
        => View("ReportPage", await BuildViewModelAsync("AuditFlags", "UAE Audit Flags Report", page));

    [HttpGet("/ReportScheduler/SubmitReport")]
    public IActionResult SubmitReport()
        => RedirectToAction(nameof(ClaimSummaryReport));

    [HttpPost("/ReportScheduler/CreateReport")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReport(ReportSchedulerViewModel model)
    {
        if (!ReportDateWindow.TryResolve(
                model.DateRange,
                Request.Form["DateFrom"].ToString(),
                Request.Form["DateTo"].ToString(),
                DateTime.Today,
                out var dateFrom,
                out var dateTo))
        {
            TempData["Error"] = "Select a valid report date range. Custom reports require both From and To dates.";
            return RedirectToAction(GetActionName(model.ReportType));
        }

        var allowedFacilityIds = await GetUserFacilityIdsAsync();
        var selectedFacilityIds = model.SelectedFacilities.Where(id => id > 0).Distinct().ToList();
        if (allowedFacilityIds != null)
        {
            selectedFacilityIds = selectedFacilityIds.Count == 0
                ? allowedFacilityIds
                : selectedFacilityIds.Where(allowedFacilityIds.Contains).ToList();
            if (selectedFacilityIds.Count == 0)
            {
                TempData["Error"] = "No authorized facility was selected for this report.";
                return RedirectToAction(GetActionName(model.ReportType));
            }
        }

        static string? Csv<T>(IEnumerable<T> values)
        {
            var items = values.Select(value => value?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            return items.Length == 0 ? null : string.Join(',', items);
        }

        var user = User.Identity?.Name ?? "system";
        var request = new ReportRequest
        {
            ReportType = model.ReportType,
            BranchId = selectedFacilityIds.FirstOrDefault() == 0 ? null : selectedFacilityIds.First(),
            ReceiverId = model.SelectedReceivers.FirstOrDefault() == 0 ? null : model.SelectedReceivers.FirstOrDefault(),
            PayerId = model.SelectedPayers.FirstOrDefault() == 0 ? null : model.SelectedPayers.FirstOrDefault(),
            ClinicianId = model.SelectedClinicians.FirstOrDefault() == 0 ? null : model.SelectedClinicians.FirstOrDefault(),
            DepartmentId = model.SelectedDepartments.FirstOrDefault() == 0 ? null : model.SelectedDepartments.FirstOrDefault(),
            EncounterType = model.EncounterTypes.FirstOrDefault(),
            FacilityIdsCsv = Csv(selectedFacilityIds),
            ReceiverIdsCsv = Csv(model.SelectedReceivers.Where(id => id > 0)),
            PayerIdsCsv = Csv(model.SelectedPayers.Where(id => id > 0)),
            ClinicianIdsCsv = Csv(model.SelectedClinicians.Where(id => id > 0)),
            DepartmentIdsCsv = Csv(model.SelectedDepartments.Where(id => id > 0)),
            EncounterTypesCsv = Csv(model.EncounterTypes),
            DateFrom = dateFrom,
            DateTo = dateTo,
            SearchCriteria = model.SearchCriteria,
            Template = model.Template,
            FileFormat = "Excel",
            RequestedBy = user,
            EmailTo = string.IsNullOrWhiteSpace(model.EmailTo) ? null : model.EmailTo.Trim()
        };

        var reportId = await _reportService.QueueReportAsync(request, model.DateRange);
        TempData["Success"] = $"Report {reportId} is queued for parsing, generation, and validation.";

        return RedirectToAction(GetActionName(model.ReportType));
    }

    [HttpGet]
    public async Task<IActionResult> GetReports(string reportType, int page = 1, int pageSize = 10)
    {
        var facilityIds = await GetUserFacilityIdsAsync();
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, pageSize, facilityIds);
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
    public async Task<IActionResult> GetGenerationStatus(string reportType)
    {
        if (string.IsNullOrWhiteSpace(reportType))
            return BadRequest(new { message = "Report type is required." });

        var facilityIds = await GetUserFacilityIdsAsync();
        var queueQuery = _context.ReportRequests.AsNoTracking()
            .Where(report => report.ReportType == reportType)
            .Where(report => report.Status == "Pending" || report.Status == "Processing");

        if (facilityIds != null)
        {
            queueQuery = facilityIds.Count > 0
                ? queueQuery.Where(report => report.BranchId.HasValue && facilityIds.Contains(report.BranchId.Value))
                : queueQuery.Where(_ => false);
        }

        var queued = await queueQuery
            .OrderBy(report => report.RequestedAt)
            .Select(report => new { report.Id, report.ReportId, report.Status })
            .ToListAsync();

        var snapshot = ReportGenerationState.Get();
        var activeVisible = snapshot.ReportType.Equals(reportType, StringComparison.OrdinalIgnoreCase)
            && queued.Any(report => report.Id == snapshot.ReportRequestId);
        var next = queued.FirstOrDefault(report => report.Status == "Pending");

        return Json(new
        {
            isRunning = activeVisible && snapshot.IsRunning,
            reportRequestId = activeVisible ? snapshot.ReportRequestId : 0,
            reportId = activeVisible ? snapshot.ReportId : next?.ReportId ?? "",
            stage = activeVisible ? snapshot.Stage : next != null ? "Queued" : "Idle",
            message = activeVisible ? snapshot.Message : next != null ? "Waiting for the active backend report to finish." : "No report is currently running.",
            pct = activeVisible ? snapshot.Pct : 0,
            done = activeVisible ? snapshot.Done : 0,
            total = activeVisible ? snapshot.Total : 0,
            facility = activeVisible ? snapshot.Facility : "",
            dateRange = activeVisible ? snapshot.DateRange : "",
            startedAt = activeVisible ? snapshot.StartedAt : (DateTime?)null,
            pendingCount = queued.Count(report => report.Status == "Pending"),
            hasWork = queued.Count > 0
        });
    }

    [HttpGet]
    [Authorize(Roles = AppRoles.RcmAccess)]
    public async Task<IActionResult> Download(int id)
    {
        var report = await _reportService.GetReportByIdAsync(id);
        if (report == null || string.IsNullOrEmpty(report.FilePath))
            return NotFound();

        var filePath = ResolveReportFilePath(report.FilePath);
        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found on server.");

        var fileName = Path.GetFileName(filePath);
        var contentType = report.FileFormat switch
        {
            "CSV" => "text/csv",
            "PDF" => "application/pdf",
            _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

        return PhysicalFile(filePath, contentType, fileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = AppRoles.RcmAccess)]
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
    [Authorize(Roles = AppRoles.RcmAccess)]
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

    private static string? ResolveReportFilePath(string? reportFilePath)
    {
        if (string.IsNullOrWhiteSpace(reportFilePath))
            return null;

        var webRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
        var filePath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "wwwroot",
            reportFilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

        return filePath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) ? filePath : null;
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
        "AuditFlags" => "AuditFlagsReport",
        _ => "ClaimSummaryReport"
    };
}
