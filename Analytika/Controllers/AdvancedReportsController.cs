using Analytika.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

[Authorize]
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
