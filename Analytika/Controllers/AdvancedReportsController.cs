using Analytika.Models.ViewModels;
using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

[Authorize(Roles = AppRoles.ReportAccess)]
public class AdvancedReportsController : Controller
{
    // Wired into the shared ReportPage flow (recent listing, filters, submit,
    // queue/generate, download) hosted by ReportScheduler.
    public IActionResult SubmissionXMLFileReport()
        => RedirectToAction("SubmissionXMLReport", "ReportScheduler");
}
