using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Runs resource-intensive reports outside the web host. The worker and web app
/// share the durable ReportRequests queue, while report generation remains
/// serialized to protect SQLite from competing writers.
/// </summary>
public static class ExternalReportWorker
{
    public static async Task RunAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        var reportTypes = (configuration.GetSection("Reports:ExternalWorkerReportTypes").Get<string[]>() ?? ["AuditFlags"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pollSeconds = Math.Clamp(configuration.GetValue("Reports:ExternalWorkerPollSeconds", 3), 1, 60);

        logger.LogInformation(
            "External report worker started for {ReportTypes}; polling every {PollSeconds}s.",
            string.Join(", ", reportTypes), pollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            int? reportId;
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-1);
                reportId = await db.ReportRequests
                    .AsNoTracking()
                    .Where(report => reportTypes.Contains(report.ReportType))
                    .Where(report => report.FilePath == null)
                    .Where(report => report.RequestedAt >= cutoff)
                    .Where(report => report.Status == "Pending" || report.Status == "Processing")
                    .OrderBy(report => report.RequestedAt)
                    .Select(report => (int?)report.Id)
                    .FirstOrDefaultAsync(stoppingToken);
            }

            if (!reportId.HasValue)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                continue;
            }

            try
            {
                using var scope = services.CreateScope();
                var reports = scope.ServiceProvider.GetRequiredService<IReportService>();
                logger.LogInformation("External worker processing report request {ReportRequestId}.", reportId.Value);
                await reports.GenerateReportAsync(reportId.Value);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "External worker failed while processing report request {ReportRequestId}.", reportId.Value);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }
}
