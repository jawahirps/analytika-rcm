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
            var lastSuccess = await _db.PortalFetchLogs
                .Where(l => l.Status == "Success")
                .MaxAsync(l => (DateTime?)l.FetchedAt, ct);

            if (lastSuccess == null)
                return HealthCheckResult.Degraded("Credentials are configured but no portal fetch has succeeded yet.");

            var age = DateTime.UtcNow - lastSuccess.Value;
            return age > TimeSpan.FromHours(staleAfterHours)
                ? HealthCheckResult.Degraded($"Last successful portal fetch was {age.TotalHours:F0}h ago (threshold {staleAfterHours}h).")
                : HealthCheckResult.Healthy($"Last successful portal fetch {age.TotalMinutes:F0} min ago.");
        }
        catch (Exception ex)
        {
            // Schema not ready (first boot) — don't fail the probe over the sync signal
            return HealthCheckResult.Healthy($"Sync status unavailable: {ex.Message}");
        }
    }
}
