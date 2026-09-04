using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Resumes recent in-process report jobs that were interrupted by an app restart
/// or an aborted HTTP client. Report generation remains idempotent and validated.
/// </summary>
public sealed class PendingReportRecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PendingReportRecoveryService> _logger;

    public PendingReportRecoveryService(IServiceScopeFactory scopeFactory, ILogger<PendingReportRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var pendingIds = await db.ReportRequests
            .AsNoTracking()
            .Where(report => report.RequestedAt >= cutoff)
            .Where(report => report.FilePath == null)
            .Where(report => report.Status == "Pending" || report.Status == "Processing")
            .OrderBy(report => report.RequestedAt)
            .Select(report => report.Id)
            .Take(100)
            .ToListAsync(stoppingToken);

        if (pendingIds.Count == 0)
            return;

        _logger.LogWarning("Recovering {Count} interrupted report job(s).", pendingIds.Count);
        foreach (var id in pendingIds)
        {
            stoppingToken.ThrowIfCancellationRequested();
            using var reportScope = _scopeFactory.CreateScope();
            var reports = reportScope.ServiceProvider.GetRequiredService<IReportService>();
            await reports.GenerateReportAsync(id);
        }
    }
}
