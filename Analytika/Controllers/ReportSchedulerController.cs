using Analytika.Models;
using Analytika.Models.ViewModels;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Controllers;

[Authorize]
public class ReportSchedulerController : Controller
{
    private readonly AppDbContext _context;
    private readonly IReportService _reportService;

    public ReportSchedulerController(AppDbContext context, IReportService reportService)
    {
        _context = context;
        _reportService = reportService;
    }

    private async Task<ReportSchedulerViewModel> BuildViewModelAsync(string reportType, string reportTitle, int page = 1)
    {
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, 10);
        return new ReportSchedulerViewModel
        {
            ReportType = reportType,
            ReportTitle = reportTitle,
            Facilities = new SelectList(await _context.Facilities.Where(f => f.IsActive).ToListAsync(), "Id", "Name"),
            Receivers = new SelectList(await _context.Receivers.Where(r => r.IsActive).ToListAsync(), "Id", "Name"),
            Payers = new SelectList(await _context.Payers.Where(p => p.IsActive).ToListAsync(), "Id", "Name"),
            Clinicians = new SelectList(await _context.Clinicians.Where(c => c.IsActive).ToListAsync(), "Id", "Name"),
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReport(ReportSchedulerViewModel model)
    {
        var user = User.Identity?.Name ?? "system";
        var request = new ReportRequest
        {
            ReportType = model.ReportType,
            BranchId = model.SelectedFacilities.FirstOrDefault() == 0 ? null : model.SelectedFacilities.FirstOrDefault(),
            ReceiverId = model.SelectedReceivers.FirstOrDefault() == 0 ? null : model.SelectedReceivers.FirstOrDefault(),
            PayerId = model.SelectedPayers.FirstOrDefault() == 0 ? null : model.SelectedPayers.FirstOrDefault(),
            ClinicianId = model.SelectedClinicians.FirstOrDefault() == 0 ? null : model.SelectedClinicians.FirstOrDefault(),
            DepartmentId = model.SelectedDepartments.FirstOrDefault() == 0 ? null : model.SelectedDepartments.FirstOrDefault(),
            EncounterType = model.EncounterType,
            DateFrom = model.DateFrom ?? DateTime.Now.AddMonths(-1),
            DateTo = model.DateTo ?? DateTime.Now,
            SearchCriteria = model.SearchCriteria,
            Template = model.Template,
            FileFormat = model.FileFormat,
            RequestedBy = user,
            EmailTo = string.IsNullOrWhiteSpace(model.EmailTo) ? null : model.EmailTo.Trim()
        };

        var reportId = await _reportService.QueueReportAsync(request);
        TempData["Success"] = $"Report {reportId} has been queued successfully.";

        return RedirectToAction(GetActionName(model.ReportType));
    }

    [HttpGet]
    public async Task<IActionResult> GetReports(string reportType, int page = 1, int pageSize = 10)
    {
        var (reports, total) = await _reportService.GetReportsAsync(reportType, page, pageSize);
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

        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", report.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
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
        _ => "ClaimSummaryReport"
    };
}
