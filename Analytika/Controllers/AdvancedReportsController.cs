using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

/// <summary>
/// Advanced report entry points. Submission XML uses the shared Report Scheduler UI.
/// </summary>
[Authorize(Roles = AppRoles.ReportAccess)]
public class AdvancedReportsController : Controller
{
    [HttpGet]
    public IActionResult SubmissionXMLFileReport(int page = 1)
        => RedirectToAction("SubmissionXMLFileReport", "ReportScheduler", new { page });
}
