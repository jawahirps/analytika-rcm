using Analytika.Models.ViewModels;
using Analytika.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

[Authorize(Roles = AppRoles.ReportAccess)]
public class AdvancedReportsController : Controller
{
    public IActionResult SubmissionXMLFileReport()
    {
        var vm = new ReportSchedulerViewModel
        {
            ReportType = "SubmissionXML",
            ReportTitle = "Submission XML File Report"
        };
        return View(vm);
    }
}
