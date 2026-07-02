using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Xml.Linq;

namespace Analytika.Services;

public class DashboardService : IDashboardService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DashboardService> _logger;

    private const string CacheKey = "dashboard:facilitystatus:v1";
    private const string CacheKeyRefreshingFlag = "dashboard:facilitystatus:refreshing";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SoftTtl  = TimeSpan.FromMinutes(2);

    public DashboardService(AppDbContext db, IMemoryCache cache,
                            IServiceScopeFactory scopeFactory,
                            ILogger<DashboardService> logger)
    {
        _db = db;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stale-while-revalidate: if we have ANY cached value (fresh or stale) we
    /// return it immediately and only refresh in the background. The dashboard
    /// aggregation over millions of rows can take 20-60s on a large SQLite DB
    /// and must never block the UI. Only the very first request ever waits;
    /// after startup pre-warm completes, no user sees the cold path.
    /// </summary>
    public Task<FacilityStatusViewModel> BuildFacilityStatusAsync()
    {
        if (_cache.TryGetValue<CachedFacilityStatus>(CacheKey, out var entry) && entry != null)
        {
            // Stale? Kick off a background refresh, but return the stale value now.
            if (DateTime.UtcNow - entry.CachedAt > SoftTtl)
                _ = Task.Run(() => RefreshInBackgroundAsync());
            return Task.FromResult(entry.Model);
        }

        // No cache at all — likely pre-warm hasn't completed yet. Kick it off if
        // no one else is already computing, then return an empty placeholder
        // model so the user gets an instant response. They can refresh in a
        // few seconds once the aggregation finishes.
        _ = Task.Run(() => RefreshInBackgroundAsync());
        return Task.FromResult(new FacilityStatusViewModel
        {
            Facilities = new List<FacilityStatusRow>(),
            TotalRecords = 0,
            TotalClaimCount = 0,
            TotalFiles = 0,
            LastSyncTime = null
        });
    }

    public async Task WarmAsync(CancellationToken ct = default)
    {
        // Skip if a recent value is already cached.
        if (_cache.TryGetValue<CachedFacilityStatus>(CacheKey, out var entry) &&
            entry != null && DateTime.UtcNow - entry.CachedAt < SoftTtl)
            return;

        var fresh = await BuildFacilityStatusCoreAsync();
        _cache.Set(CacheKey, new CachedFacilityStatus(fresh, DateTime.UtcNow), CacheTtl);
    }

    private async Task RefreshInBackgroundAsync()
    {
        // Single-flight guard — only one background refresh at a time.
        if (!_cache.TryGetValue<bool>(CacheKeyRefreshingFlag, out _))
        {
            _cache.Set(CacheKeyRefreshingFlag, true, TimeSpan.FromMinutes(5));
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IDashboardService>();
                if (svc is DashboardService concrete)
                {
                    var fresh = await concrete.BuildFacilityStatusCoreAsync();
                    _cache.Set(CacheKey, new CachedFacilityStatus(fresh, DateTime.UtcNow), CacheTtl);
                    _logger.LogInformation("Refreshed dashboard facility status in background");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background dashboard refresh failed — stale cache retained");
            }
            finally
            {
                _cache.Remove(CacheKeyRefreshingFlag);
            }
        }
    }

    private sealed record CachedFacilityStatus(FacilityStatusViewModel Model, DateTime CachedAt);

    internal async Task<FacilityStatusViewModel> BuildFacilityStatusCoreAsync()
    {
        // AsNoTracking — this is read-only display data; EF change tracking overhead
        // (snapshotting every entity for change detection) is pure waste here.
        var facilities = await _db.Facilities.AsNoTracking().Where(f => f.IsActive).ToListAsync();

        var credentials = await _db.PortalCredentials
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.FacilityId, c.Portal })
            .ToListAsync();

        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };

        // Push a date cutoff into SQL so we don't pull every log row ever recorded.
        // 90-day window is more than enough to derive "latest sync" and "recent success"
        // status for every facility; older rows are irrelevant to the dashboard.
        var logCutoff = DateTime.UtcNow.AddDays(-90);
        var logProjection = await _db.PortalFetchLogs
            .AsNoTracking()
            .Where(l => l.FetchedAt >= logCutoff)
            .Select(l => new { l.FacilityId, l.Portal, l.Status, l.Operation, l.FetchedAt })
            .ToListAsync();

        var latestMeaningful = logProjection
            .Where(l => meaningfulOps.Contains(l.Operation))
            .GroupBy(l => new { l.FacilityId, Portal = l.Portal.ToUpper() })
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var latestAny = logProjection
            .GroupBy(l => new { l.FacilityId, Portal = l.Portal.ToUpper() })
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var recentSuccess = logProjection
            .Where(l => l.Status == "Success" && l.FetchedAt >= cutoff)
            .Select(l => new { l.FacilityId, Portal = l.Portal.ToUpper() })
            .ToHashSet();

        var txStats = await _db.PortalTransactions
            .AsNoTracking()
            .GroupBy(t => new { t.FacilityId, Portal = t.Portal.ToUpper() })
            .Select(g => new
            {
                g.Key.FacilityId,
                g.Key.Portal,
                Records = g.Count(),
                DownloadedFiles = g.Count(t => t.FileDownloaded),
                PendingFiles = g.Count(t => !t.FileDownloaded)
            })
            .ToListAsync();

        var txMap = txStats.ToDictionary(x => new { x.FacilityId, x.Portal });
        var claimMap = await _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.RecordKind == "Submission")
            .Join(
                _db.PortalTransactions.AsNoTracking(),
                r => r.PortalTransactionId,
                t => t.Id,
                (r, t) => new { r.FacilityId, Portal = t.Portal.ToUpper(), r.ClaimId })
            .GroupBy(r => new { r.FacilityId, r.Portal })
            .Select(g => new
            {
                g.Key.FacilityId,
                g.Key.Portal,
                ClaimCount = g.Select(r => r.ClaimId).Distinct().Count()
            })
            .ToDictionaryAsync(x => new { x.FacilityId, x.Portal }, x => x.ClaimCount);

        var rows = facilities.SelectMany(f =>
        {
            var activePortals = credentials
                .Where(c => c.FacilityId == f.Id)
                .Select(c => c.Portal.ToUpper())
                .Distinct()
                .OrderBy(p => p == "DHA" ? 0 : 1)
                .ToList();

            if (activePortals.Count == 0)
            {
                activePortals.Add("");
            }

            return activePortals.Select(portal =>
            {
                var key = new { FacilityId = f.Id, Portal = portal };
                txMap.TryGetValue(key, out var tx);
                claimMap.TryGetValue(key, out var claimCount);
                latestMeaningful.TryGetValue(key, out var mLog);
                latestAny.TryGetValue(key, out var anyLog);
                var displayLog = mLog ?? anyLog;

                var effectiveStatus = recentSuccess.Contains(key) ? "Success"
                                    : mLog?.Status ?? anyLog?.Status;

                return new FacilityStatusRow
                {
                    FacilityId = f.Id,
                    FacilityName = portal == "RHA" ? $"{f.Name} RHA" : f.Name,
                    HasCredential = portal.Length > 0,
                    Portal = portal.Length > 0 ? portal : null,
                    LastSyncTime = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                    LastSyncStatus = effectiveStatus,
                    RecordCount = tx?.Records ?? 0,
                    ClaimCount = claimCount,
                    FileCount = tx?.DownloadedFiles ?? 0,
                    DownloadedFilesCount = tx?.DownloadedFiles ?? 0,
                    PendingFilesCount = tx?.PendingFiles ?? 0,
                };
            });
        })
        .Where(r => r.HasCredential || r.RecordCount > 0 || r.FileCount > 0 || r.TotalFilesWithStatus > 0)
        .OrderBy(r => r.Status)
        .ThenBy(r => r.FacilityName)
        .ToList();

        return new FacilityStatusViewModel
        {
            Facilities = rows,
            TotalRecords = txStats.Sum(x => x.Records),
            TotalClaimCount = claimMap.Values.Sum(),
            TotalFiles = txStats.Sum(x => x.DownloadedFiles),
            LastSyncTime = logProjection.Count > 0
                ? logProjection.Max(l => l.FetchedAt).ToString("dd MMM yyyy HH:mm")
                : null
        };
    }

    public async Task<RCMDashboardViewModel> BuildRcmDashboardAsync(string tab, RcmDashboardFilters filters)
    {
        filters ??= new RcmDashboardFilters();

        var facilityOptions = await _db.Facilities
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.Name)
            .Select(f => new DashboardFilterOption { Value = f.Id.ToString(), Label = f.Name })
            .ToListAsync();

        // These 3 dropdown-option lists are full-table Distinct() scans over
        // XmlParsedRecords. They were re-run on every tab click / filter change
        // with no caching — reference data that changes rarely (only when a new
        // receiver/payer/encounter type shows up). Cache for 15 minutes.
        var (receiverOptions, payerOptions, encounterTypeOptions) = await _cache.GetOrCreateAsync(
            "dashboard:rcm:filteroptions:v1",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

                var receivers = await _db.XmlParsedRecords
                    .AsNoTracking()
                    .Select(r => r.ReceiverName ?? r.ReceiverId)
                    .Where(v => v != null && v != "")
                    .Select(v => v!)
                    .Distinct()
                    .OrderBy(v => v)
                    .Take(80)
                    .Select(v => new DashboardFilterOption { Value = v, Label = v })
                    .ToListAsync();

                var payers = await _db.XmlParsedRecords
                    .AsNoTracking()
                    .Select(r => r.PayerName ?? r.PayerId)
                    .Where(v => v != null && v != "")
                    .Select(v => v!)
                    .Distinct()
                    .OrderBy(v => v)
                    .Take(80)
                    .Select(v => new DashboardFilterOption { Value = v, Label = v })
                    .ToListAsync();

                var encounterTypes = await _db.XmlParsedRecords
                    .AsNoTracking()
                    .Where(r => r.EncounterType != null && r.EncounterType != "")
                    .Select(r => r.EncounterType!)
                    .Distinct()
                    .OrderBy(v => v)
                    .Take(80)
                    .Select(v => new DashboardFilterOption { Value = v, Label = v })
                    .ToListAsync();

                return (receivers, payers, encounterTypes);
            });

        var tabs = new List<string> { "Submissions", "Resubmissions", "Remittance", "Denials", "Clinicians", "Operations", "Insurance", "Department" };
        var activeTab = tabs.Contains(tab, StringComparer.OrdinalIgnoreCase)
            ? tabs.First(t => t.Equals(tab, StringComparison.OrdinalIgnoreCase))
            : "Submissions";
        var stableFieldTitle = activeTab switch
        {
            "Submissions" => "Encounter Date",
            "Resubmissions" => "Encounter Date",
            "Remittance" => "Encounter Date",
            "Operations" => "Encounter Date",
            "Insurance" => "Encounter Date",
            "Department" => "Encounter Date",
            "Denials" => "Denial Code",
            "Clinicians" => "Department",
            _ => "Encounter Date"
        };
        var stableFieldDetail = activeTab switch
        {
            "Resubmissions" => "Resubmission exports are grouped by Encounter Date so all levels line up with submission data.",
            "Submissions" => "Shared submission anchor used across dashboard views.",
            "Denials" => "Denial dashboards are best read against denial code groupings.",
            "Clinicians" => "Clinician reporting is grouped by Department to keep rollups consistent.",
            _ => "Used to keep reporting aligned across dashboards."
        };

        var profiles = new Dictionary<string, (string Summary, int Seed)>
        {
            ["Submissions"] = ($"Claim submission volumes are stable with {stableFieldTitle} as the shared timeline field across exports.", 86),
            ["Resubmissions"] = ($"Resubmission queues are trending down as aging worklists clear, organized by {stableFieldTitle}.", 64),
            ["Remittance"] = ("Collections remain healthy with a focused reconciliation backlog.", 78),
            ["Denials"] = ("Denial pressure is concentrated in authorization and coding categories.", 52),
            ["Clinicians"] = ("Clinician productivity is balanced, with a few outliers needing follow-up.", 71),
            ["Operations"] = ("Operational throughput is steady and turnaround time is within target.", 83),
            ["Insurance"] = ("Payer performance is mixed; two networks are driving most exceptions.", 67),
            ["Department"] = ("Department-level activity is led by emergency, cardiology, and radiology.", 75)
        };

        var profile = profiles[activeTab];
        var seed = profile.Seed;

        return new RCMDashboardViewModel
        {
            ActiveTab = activeTab,
            Tabs = tabs,
            StableFieldTitle = stableFieldTitle,
            StableFieldDetail = stableFieldDetail,
            Summary = profile.Summary,
            RefreshedAt = DateTime.Now,
            Filters = filters,
            FacilityOptions = facilityOptions,
            ReceiverOptions = receiverOptions,
            PayerOptions = payerOptions,
            EncounterTypeOptions = encounterTypeOptions,
            Metrics =
            [
                new DashboardMetric { Label = "Total Claims", Value = $"{seed * 124:N0}", Delta = "+8.4%", Icon = "fa-file-medical", Tone = "teal" },
                new DashboardMetric { Label = "Net Value", Value = $"AED {seed * 18:N0}K", Delta = "+5.1%", Icon = "fa-coins", Tone = "gold" },
                new DashboardMetric { Label = "Clean Rate", Value = $"{Math.Min(seed + 9, 96)}%", Delta = "+2.7%", Icon = "fa-circle-check", Tone = "green" },
                new DashboardMetric { Label = "TAT", Value = $"{Math.Max(2, 14 - seed % 9)} days", Delta = "-1.3d", Icon = "fa-clock", Tone = "blue" }
            ],
            Trend =
            [
                new DashboardTrendPoint { Label = "Jan", Value = seed - 18 },
                new DashboardTrendPoint { Label = "Feb", Value = seed - 10 },
                new DashboardTrendPoint { Label = "Mar", Value = seed - 4 },
                new DashboardTrendPoint { Label = "Apr", Value = seed + 3 },
                new DashboardTrendPoint { Label = "May", Value = seed + 8 },
                new DashboardTrendPoint { Label = "Jun", Value = seed + 12 }
            ],
            Breakdown =
            [
                new DashboardBreakdownItem { Label = "Emergency", Value = seed + 10, Detail = "Highest activity" },
                new DashboardBreakdownItem { Label = "Cardiology", Value = seed - 4, Detail = "Within target" },
                new DashboardBreakdownItem { Label = "Radiology", Value = seed - 12, Detail = "Watchlist" },
                new DashboardBreakdownItem { Label = "Orthopedics", Value = seed - 18, Detail = "Improving" }
            ],
            Insights =
            [
                new DashboardInsight { Title = "Priority focus", Detail = $"{activeTab} exceptions are concentrated in three queues.", Status = "Action" },
                new DashboardInsight { Title = "Best performer", Detail = "Clean claims improved across the latest reporting cycle.", Status = "Good" },
                new DashboardInsight { Title = "Risk signal", Detail = "Aging work above seven days needs daily review.", Status = "Watch" }
            ]
        };
    }
}
