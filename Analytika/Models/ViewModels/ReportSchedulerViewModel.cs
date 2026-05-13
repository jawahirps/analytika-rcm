using Microsoft.AspNetCore.Mvc.Rendering;

namespace Analytika.Models.ViewModels;

public class ReportSchedulerViewModel
{
    public string ReportType { get; set; } = string.Empty;
    public string ReportTitle { get; set; } = string.Empty;
    public string DateRange { get; set; } = "ThisMonth";
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? SearchCriteria { get; set; } = "EncounterStartDate";
    public List<int> SelectedFacilities { get; set; } = new();
    public List<int> SelectedReceivers { get; set; } = new();
    public List<int> SelectedPayers { get; set; } = new();
    public List<int> SelectedClinicians { get; set; } = new();
    public List<int> SelectedDepartments { get; set; } = new();
    public string? EncounterType { get; set; }
    public string? Template { get; set; }
    public string FileFormat { get; set; } = "Excel";
    public string? EmailTo { get; set; }   // comma-separated recipient addresses

    public SelectList? Facilities { get; set; }
    public SelectList? Receivers { get; set; }
    public SelectList? Payers { get; set; }
    public SelectList? Clinicians { get; set; }
    public SelectList? Departments { get; set; }

    public List<ReportRequest> RecentReports { get; set; } = new();
    public int TotalReports { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalPages => (int)Math.Ceiling((double)TotalReports / PageSize);
}
