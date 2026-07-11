using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Analytika.Services;

/// <summary>
/// Reports Degraded when no portal fetch has succeeded within the configured
/// window while active credentials exist — the primary SLA signal for a pod.
/// Degraded (not Unhealthy) so hosting platforms don't restart pods over
/// portal-side outages.
/// </summary>
public class SyncHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public SyncHealthCheck(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var activeCreds = await _db.PortalCredentials.CountAsync(c => c.IsActive, ct);
            if (activeCreds == 0)
                return HealthCheckResult.Healthy("No active portal credentials configured.");

            var staleAfterHours = _config.GetValue("Monitoring:SyncStaleAfterHours", 26);

            var facilityStats = await _db.PortalCredentials
                .Where(c => c.IsActive)
                .Select(c => new { c.FacilityId, FacilityName = c.Facility!.Name, c.Portal })
                .Distinct()
                .ToListAsync(ct);

            var cutoff90d = DateTime.UtcNow.AddDays(-90);
            var recentLogs = await _db.PortalFetchLogs
                .Where(l => l.FetchedAt >= cutoff90d)
                .GroupBy(l => new { l.FacilityId, l.Portal })
                .Select(g => new
                {
                    g.Key.FacilityId,
                    g.Key.Portal,
                    LastSuccess = g.Where(l => l.Status == "Success").Max(l => (DateTime?)l.FetchedAt),
                    LastAttempt = g.Max(l => l.FetchedAt),
                    FailCount = g.Count(l => l.Status != "Success")
                })
                .ToListAsync(ct);

            var pendingDownloads = await _db.PortalTransactions
                .CountAsync(t => !t.FileDownloaded, ct);

            var parsedCount = await _db.XmlParsedRecords.CountAsync(ct);

            var data = new Dictionary<string, object>
            {
                ["activeFacilities"] = facilityStats.Count,
                ["pendingDownloads"] = pendingDownloads,
                ["parsedRecords"] = parsedCount
            };

            var degradedFacilities = new List<string>();
            foreach (var fac in facilityStats)
            {
                var log = recentLogs.FirstOrDefault(l =>
                    l.FacilityId == fac.FacilityId &&
                    l.Portal.Equals(fac.Portal, StringComparison.OrdinalIgnoreCase));

                var key = $"{fac.FacilityName}:{fac.Portal}";
                if (log?.LastSuccess == null)
                {
                    degradedFacilities.Add($"{key} (never synced)");
                    data[$"facility:{key}:status"] = "never_synced";
                }
                else
                {
                    var age = DateTime.UtcNow - log.LastSuccess.Value;
                    data[$"facility:{key}:lastSuccess"] = $"{age.TotalHours:F0}h ago";
                    data[$"facility:{key}:recentFailures"] = log.FailCount;
                    if (age > TimeSpan.FromHours(staleAfterHours))
                        degradedFacilities.Add($"{key} ({age.TotalHours:F0}h stale)");
                }
            }

            if (degradedFacilities.Count > 0)
                return HealthCheckResult.Degraded(
                    $"{degradedFacilities.Count}/{facilityStats.Count} facilities degraded: {string.Join(", ", degradedFacilities.Take(5))}",
                    data: data);

            return HealthCheckResult.Healthy(
                $"All {facilityStats.Count} facilities synced within {staleAfterHours}h. {pendingDownloads} pending downloads, {parsedCount:N0} parsed records.",
                data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Healthy($"Sync status unavailable: {ex.Message}");
        }
    }
}
