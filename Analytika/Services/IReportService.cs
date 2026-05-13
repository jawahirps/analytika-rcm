using Analytika.Models;

namespace Analytika.Services;

public interface IReportService
{
    Task<string> QueueReportAsync(ReportRequest request, string? selectedDateRange = null);
    Task<(List<ReportRequest> Reports, int Total)> GetReportsAsync(string reportType, int page, int pageSize);
    Task<ReportRequest?> GetReportByIdAsync(int id);
    Task GenerateReportAsync(int reportRequestId);
    string GetNextReportId(ReportRequest request, string? selectedDateRange = null);
}
