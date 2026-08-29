namespace Analytika.Models;

/// <summary>
/// Defines a recurring automated report that is generated and emailed on a schedule.
/// The CronExpression drives a Hangfire RecurringJob keyed on "schedule-{Id}".
/// </summary>
public class ReportSchedule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;   // friendly label
    public string ReportType { get; set; } = string.Empty;   // matches ReportSchedulerViewModel.ReportType
    public string CronExpression { get; set; } = "0 8 1 * *";   // "At 08:00 on day-of-month 1"
    public string Recipients { get; set; } = string.Empty;   // comma-separated emails
    public string FileFormat { get; set; } = "Excel";
    public string? FacilityIdsJson { get; set; }   // JSON int array, null = all
    public string? ParametersJson { get; set; }   // extra JSON (date range overrides, etc.)
    public bool IsActive { get; set; } = true;
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }    // "OK" | "Error: …"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
