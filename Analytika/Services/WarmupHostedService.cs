using Microsoft.Extensions.DependencyInjection;

namespace Analytika.Services;

/// <summary>
/// Keeps the heavy dashboard aggregation cache — and the SQLite hot-page cache it reads
/// — permanently warm, so the app stays fast even after long idle periods (not only while
/// someone is actively clicking around on localhost).
///
/// Why this exists: <see cref="BixKeepaliveService"/> pings <c>/healthz</c>, which keeps
/// Kestrel and the Cloudflare tunnel alive but does ZERO database work. During idle the
/// facility-status cache (10-min TTL) expires and Windows evicts the parsed tables' pages
/// from RAM, so the first real request pays a full cold rebuild — the "slow unless the app
/// is being used" symptom. This service re-runs the aggregation on a steady interval
/// (comfortably under the cache TTL), which both refreshes the cache and touches the hot
/// tables so their pages stay resident. Cost is one aggregation every few minutes.
///
/// Disable with Warmup:Enabled=false; tune with Warmup:IntervalSeconds.
/// </summary>
public class WarmupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<WarmupHostedService> _logger;

    public WarmupHostedService(IServiceScopeFactory scopes, IConfiguration config, ILogger<WarmupHostedService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Default 240s: over the 2-min soft-TTL (so each cycle actually rebuilds) and well
        // under the 10-min hard TTL (so the cache never fully expires and goes cold).
        var interval = TimeSpan.FromSeconds(Math.Max(60, _config.GetValue("Warmup:IntervalSeconds", 240)));
        _logger.LogInformation("[Warmup] Dashboard cache warm-up started — every {sec}s.", interval.TotalSeconds);

        // Small initial delay so startup's own WarmAsync and pre-warm finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var dashboard = scope.ServiceProvider.GetRequiredService<IDashboardService>();
                await dashboard.WarmAsync(stoppingToken);   // rebuilds facility-status cache + touches the hot parsed/portal tables
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Warmup] cycle failed — existing cache left in place.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
