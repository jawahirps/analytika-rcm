using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace Analytika.Services;

public class DashboardService : IDashboardService
{
    private readonly AppDbContext _db;

    public DashboardService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<FacilityStatusViewModel> BuildFacilityStatusAsync()
    {
        var facilities = await _db.Facilities.Where(f => f.IsActive).ToListAsync();

        var credsByFacility = await _db.PortalCredentials
            .Where(c => c.IsActive)
            .GroupBy(c => c.FacilityId)
            .Select(g => new { FacilityId = g.Key, Portals = g.Select(c => c.Portal).ToList() })
            .ToListAsync();

        var meaningfulOps = new[] { "CronSync", "MonthWiseSync", "BulkSave", "SyncAll2Y" };
        var logProjection = await _db.PortalFetchLogs
            .AsNoTracking()
            .Select(l => new { l.FacilityId, l.Status, l.Operation, l.FetchedAt })
            .ToListAsync();

        var latestMeaningful = logProjection
            .Where(l => meaningfulOps.Contains(l.Operation))
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var latestAny = logProjection
            .GroupBy(l => l.FacilityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FetchedAt).First());

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var recentSuccess = logProjection
            .Where(l => l.Status == "Success" && l.FetchedAt >= cutoff)
            .Select(l => l.FacilityId)
            .ToHashSet();

        var txStats = await _db.PortalTransactions
            .AsNoTracking()
            .GroupBy(t => t.FacilityId)
            .Select(g => new
            {
                FacilityId = g.Key,
                Records = g.Count(),
                DownloadedFiles = g.Count(t => t.FileDownloaded),
                PendingFiles = g.Count(t => !t.FileDownloaded)
            })
            .ToListAsync();

        var credMap = credsByFacility.ToDictionary(x => x.FacilityId);
        var txMap = txStats.ToDictionary(x => x.FacilityId);
        var claimMap = await _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.RecordKind == "Submission")
            .GroupBy(r => r.FacilityId)
            .Select(g => new
            {
                FacilityId = g.Key,
                ClaimCount = g.Select(r => r.ClaimId).Distinct().Count()
            })
            .ToDictionaryAsync(x => x.FacilityId, x => x.ClaimCount);

        var rows = facilities.Select(f =>
        {
            credMap.TryGetValue(f.Id, out var cred);
            txMap.TryGetValue(f.Id, out var tx);
            claimMap.TryGetValue(f.Id, out var claimCount);
            latestMeaningful.TryGetValue(f.Id, out var mLog);
            latestAny.TryGetValue(f.Id, out var anyLog);
            var displayLog = mLog ?? anyLog;

            var effectiveStatus = recentSuccess.Contains(f.Id) ? "Success"
                                : mLog?.Status ?? anyLog?.Status;

            return new FacilityStatusRow
            {
                FacilityId = f.Id,
                FacilityName = f.Name,
                HasCredential = cred != null,
                Portal = cred != null ? string.Join(" · ", cred.Portals.Distinct()) : null,
                LastSyncTime = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                LastSyncStatus = effectiveStatus,
                RecordCount = tx?.Records ?? 0,
                ClaimCount = claimCount,
                FileCount = tx?.DownloadedFiles ?? 0,
                DownloadedFilesCount = tx?.DownloadedFiles ?? 0,
                PendingFilesCount = tx?.PendingFiles ?? 0,
            };
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

    public RCMDashboardViewModel BuildRcmDashboard(string tab)
    {
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
