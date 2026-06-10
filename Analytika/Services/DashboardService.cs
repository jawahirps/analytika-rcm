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

    public DashboardService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public Task<FacilityStatusViewModel> BuildFacilityStatusAsync()
        => _cache.GetOrCreateAsync("dashboard:facilitystatus:v1", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await BuildFacilityStatusCoreAsync();
        })!;

    private async Task<FacilityStatusViewModel> BuildFacilityStatusCoreAsync()
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        var credentials = await _db.PortalCredentials
            .Where(c => c.IsActive)
            .Select(c => new { c.FacilityId, c.Portal })
            .ToListAsync();

        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };
        var logProjection = await _db.PortalFetchLogs
            .AsNoTracking()
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

        var receiverOptions = await _db.XmlParsedRecords
            .AsNoTracking()
            .Select(r => r.ReceiverName ?? r.ReceiverId)
            .Where(v => v != null && v != "")
            .Select(v => v!)
            .Distinct()
            .OrderBy(v => v)
            .Take(80)
            .Select(v => new DashboardFilterOption { Value = v, Label = v })
            .ToListAsync();

        var payerOptions = await _db.XmlParsedRecords
            .AsNoTracking()
            .Select(r => r.PayerName ?? r.PayerId)
            .Where(v => v != null && v != "")
            .Select(v => v!)
            .Distinct()
            .OrderBy(v => v)
            .Take(80)
            .Select(v => new DashboardFilterOption { Value = v, Label = v })
            .ToListAsync();

        var encounterTypeOptions = await _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.EncounterType != null && r.EncounterType != "")
            .Select(r => r.EncounterType!)
            .Distinct()
            .OrderBy(v => v)
            .Take(80)
            .Select(v => new DashboardFilterOption { Value = v, Label = v })
            .ToListAsync();

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
