using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Data.Common;
using System.Xml.Linq;

namespace Analytika.Services;

public class DashboardService : IDashboardService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DashboardService> _logger;
    private readonly IConfiguration _config;

    private const string CacheKey = "dashboard:facilitystatus:v1";
    private const string CacheKeyRefreshingFlag = "dashboard:facilitystatus:refreshing";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SoftTtl  = TimeSpan.FromMinutes(2);

    public DashboardService(AppDbContext db, IMemoryCache cache,
                            IServiceScopeFactory scopeFactory,
                            ILogger<DashboardService> logger,
                            IConfiguration config)
    {
        _db = db;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
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

        // No memory cache — cold start. Before falling back to an empty placeholder,
        // serve the last snapshot persisted to disk (stale-while-revalidate): after a
        // restart the full rebuild takes minutes on this DB, and users were staring at
        // "No active facilities found" the whole time. Slightly stale numbers now,
        // fresh ones a refresh later, blank screen never.
        _ = Task.Run(() => RefreshInBackgroundAsync());

        var snapshot = TryLoadSnapshot();
        if (snapshot != null)
        {
            // Mark as already-stale so the background refresh continues to run.
            _cache.Set(CacheKey, new CachedFacilityStatus(snapshot, DateTime.UtcNow - CacheTtl + TimeSpan.FromMinutes(1)), CacheTtl);
            return Task.FromResult(snapshot);
        }

        // No snapshot either (first ever run). Flag it as building so the view renders a
        // loading state instead of claiming there are no facilities.
        return Task.FromResult(new FacilityStatusViewModel
        {
            Facilities = new List<FacilityStatusRow>(),
            TotalRecords = 0,
            TotalClaimCount = 0,
            TotalFiles = 0,
            LastSyncTime = null,
            IsBuilding = true
        });
    }

    // ── Disk snapshot: survives restarts so the dashboard is never blank ─────────
    // Data:Dir is the directory startup actually opened. Deriving this from DB_DIR
    // instead put the dev instance's snapshot in production's data folder, because
    // DB_DIR is set machine-wide on this host.
    private string SnapshotPath =>
        Path.Combine(_config["Data:Dir"]
                     ?? Environment.GetEnvironmentVariable("DB_DIR")
                     ?? AppContext.BaseDirectory,
                     "dashboard-snapshot.json");

    private void PersistSnapshot(FacilityStatusViewModel model)
    {
        try
        {
            File.WriteAllText(SnapshotPath, System.Text.Json.JsonSerializer.Serialize(model));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist dashboard snapshot"); }
    }

    private FacilityStatusViewModel? TryLoadSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<FacilityStatusViewModel>(File.ReadAllText(SnapshotPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load dashboard snapshot — serving empty until warm");
            return null;
        }
    }

    public async Task WarmAsync(CancellationToken ct = default)
    {
        // Skip if a recent value is already cached.
        if (_cache.TryGetValue<CachedFacilityStatus>(CacheKey, out var entry) &&
            entry != null && DateTime.UtcNow - entry.CachedAt < SoftTtl)
            return;

        var fresh = await BuildFacilityStatusCoreAsync();
        _cache.Set(CacheKey, new CachedFacilityStatus(fresh, DateTime.UtcNow), CacheTtl);
        PersistSnapshot(fresh);
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
                    PersistSnapshot(fresh);
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

        // Raw index-only SQL. Two hard-won lessons baked in:
        //  1. GroupBy(t.Portal.ToUpper()) defeated every index and forced full scans of
        //     the 40GB blob-bearing table — historical builds took up to 147 MINUTES
        //     ("Pre-warmed dashboard caches in 8814383 ms"). Portal is stored uppercase
        //     ('DHA'/'RHA'); grouping on the raw column lets SQLite answer the whole
        //     aggregate from IX_PortalTransactions_PendingDl (Portal, FileDownloaded,
        //     FacilityId, …) without touching a single table row.
        //  2. The XmlParsedRecords→PortalTransactions join existed only to attribute a
        //     Portal to parsed rows. All parsed rows are DHA today (RHA parses will land
        //     with FacilityId too); attributing 'DHA' directly removes a 1.6M-row join
        //     from every rebuild. Revisit when RHA parsing goes live.
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        var txStats = new List<(int FacilityId, string Portal, int Records, int DownloadedFiles, int PendingFiles)>();
        using (var cmd = conn.CreateCommand())
        {
            // INDEXED BY forces the covering index: left free, the planner preferred
            // IX_PortalTransactions_FacilityId_FileDownloaded (matches the GROUP BY sort)
            // and did 1.17M row-fetches into the blob table — the crawl all over again.
            // With PendingDl (Portal, FileDownloaded, FacilityId, Id) the whole aggregate
            // is answered inside the index. SQLite-only syntax; prod runs SQLite.
            cmd.CommandText = @"SELECT ""FacilityId"", ""Portal"", COUNT(*),
                                       SUM(CASE WHEN ""FileDownloaded"" THEN 1 ELSE 0 END),
                                       SUM(CASE WHEN ""FileDownloaded"" THEN 0 ELSE 1 END)
                                FROM ""PortalTransactions"" INDEXED BY ""IX_PortalTransactions_PendingDl""
                                GROUP BY ""Portal"", ""FacilityId""";
            cmd.CommandTimeout = 600;
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                txStats.Add((rdr.GetInt32(0), rdr.GetString(1).ToUpperInvariant(), rdr.GetInt32(2), rdr.GetInt32(3), rdr.GetInt32(4)));
        }
        var txMap = txStats.ToDictionary(
            x => new { x.FacilityId, x.Portal },
            x => new { x.Records, x.DownloadedFiles, x.PendingFiles });

        var claimMapTyped = new Dictionary<(int, string), int>();
        var parsedMapTyped = new Dictionary<(int, string), int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT ""FacilityId"",
                                       COUNT(DISTINCT ""PortalTransactionId""),
                                       COUNT(DISTINCT CASE WHEN ""RecordKind""='Submission' THEN UPPER(""ClaimId"") END)
                                FROM ""XmlParsedRecords""
                                GROUP BY ""FacilityId""";
            cmd.CommandTimeout = 600;
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                parsedMapTyped[(rdr.GetInt32(0), "DHA")] = rdr.GetInt32(1);
                claimMapTyped[(rdr.GetInt32(0), "DHA")] = rdr.GetInt32(2);
            }
        }

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
                claimMapTyped.TryGetValue((f.Id, portal), out var claimCount);
                parsedMapTyped.TryGetValue((f.Id, portal), out var parsedCount);
                latestMeaningful.TryGetValue(key, out var mLog);
                latestAny.TryGetValue(key, out var anyLog);
                var displayLog = mLog ?? anyLog;

                var effectiveStatus = recentSuccess.Contains(key) ? "Success"
                                    : mLog?.Status ?? anyLog?.Status;

                return new FacilityStatusRow
                {
                    FacilityId = f.Id,
                    FacilityName = portal == "RHA" ? $"{f.Name} RHA" : f.Name,
                    FullName = f.FullName,
                    LicenseCode = f.LicenseCode,
                    HasCredential = portal.Length > 0,
                    Portal = portal.Length > 0 ? portal : null,
                    LastSyncTime = displayLog?.FetchedAt.ToString("dd MMM yyyy HH:mm"),
                    LastSyncStatus = effectiveStatus,
                    RecordCount = tx?.Records ?? 0,
                    ClaimCount = claimCount,
                    FileCount = tx?.DownloadedFiles ?? 0,
                    DownloadedFilesCount = tx?.DownloadedFiles ?? 0,
                    PendingFilesCount = tx?.PendingFiles ?? 0,
                    ParsedFilesCount = parsedCount,
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
            TotalClaimCount = claimMapTyped.Values.Sum(),
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

                // Index-backed DISTINCTs. The old form projected `Name ?? Id` BEFORE
                // DISTINCT, and a COALESCE expression cannot use any index — so each of
                // these three was a full 1.6M-row table scan. Startup pre-warm timings
                // showed this dominating everything else (4-6 min typical, 57 min under
                // I/O contention). Splitting the coalesce into two indexable queries lets
                // SQLite answer each from a covering index:
                //   IX_XmlParsedRecords_Receiver (ReceiverName, ReceiverId)
                //   IX_XmlParsedRecords_Payer    (PayerName, PayerId)
                //   IX_XmlParsedRecords_Encounter(EncounterType)
                var conn = _db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

                async Task<List<string>> DistinctAsync(string sql)
                {
                    var list = new List<string>();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 300;
                    using var rdr = await cmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                        if (!rdr.IsDBNull(0)) list.Add(rdr.GetString(0));
                    return list;
                }

                // Prefer the human-readable name; fall back to the code only for rows
                // that carry no name (same result the coalesce produced).
                var receivers = (await DistinctAsync(
                        @"SELECT DISTINCT ""ReceiverName"" FROM ""XmlParsedRecords""
                          WHERE ""ReceiverName"" IS NOT NULL AND ""ReceiverName"" <> '' ORDER BY 1 LIMIT 80"))
                    .Concat(await DistinctAsync(
                        @"SELECT DISTINCT ""ReceiverId"" FROM ""XmlParsedRecords""
                          WHERE (""ReceiverName"" IS NULL OR ""ReceiverName"" = '')
                            AND ""ReceiverId"" IS NOT NULL AND ""ReceiverId"" <> '' ORDER BY 1 LIMIT 80"))
                    .Distinct().OrderBy(v => v).Take(80)
                    .Select(v => new DashboardFilterOption { Value = v, Label = v }).ToList();

                var payers = (await DistinctAsync(
                        @"SELECT DISTINCT ""PayerName"" FROM ""XmlParsedRecords""
                          WHERE ""PayerName"" IS NOT NULL AND ""PayerName"" <> '' ORDER BY 1 LIMIT 80"))
                    .Concat(await DistinctAsync(
                        @"SELECT DISTINCT ""PayerId"" FROM ""XmlParsedRecords""
                          WHERE (""PayerName"" IS NULL OR ""PayerName"" = '')
                            AND ""PayerId"" IS NOT NULL AND ""PayerId"" <> '' ORDER BY 1 LIMIT 80"))
                    .Distinct().OrderBy(v => v).Take(80)
                    .Select(v => new DashboardFilterOption { Value = v, Label = v }).ToList();

                var encounterTypes = (await DistinctAsync(
                        @"SELECT DISTINCT ""EncounterType"" FROM ""XmlParsedRecords""
                          WHERE ""EncounterType"" IS NOT NULL AND ""EncounterType"" <> '' ORDER BY 1 LIMIT 80"))
                    .Select(v => new DashboardFilterOption { Value = v, Label = v }).ToList();

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

        // ── Build WHERE clause for filtered base query ──
        var whereClauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (filters.FacilityId.HasValue)
        {
            whereClauses.Add("\"FacilityId\" = @facilityId");
            parameters.Add(("@facilityId", filters.FacilityId.Value));
        }
        if (!string.IsNullOrWhiteSpace(filters.Receiver))
        {
            whereClauses.Add("(\"ReceiverName\" IS NOT NULL AND \"ReceiverName\" <> '' AND \"ReceiverName\" = @receiver OR \"ReceiverName\" IS NULL OR \"ReceiverName\" = '') AND \"ReceiverId\" = @receiver)");
            parameters.Add(("@receiver", filters.Receiver));
        }
        if (!string.IsNullOrWhiteSpace(filters.Payer))
        {
            whereClauses.Add("(\"PayerName\" IS NOT NULL AND \"PayerName\" <> '' AND \"PayerName\" = @payer OR \"PayerName\" IS NULL OR \"PayerName\" = '') AND \"PayerId\" = @payer)");
            parameters.Add(("@payer", filters.Payer));
        }
        if (!string.IsNullOrWhiteSpace(filters.EncounterType))
        {
            whereClauses.Add("\"EncounterType\" = @encounterType");
            parameters.Add(("@encounterType", filters.EncounterType));
        }
        if (filters.DateFrom.HasValue)
        {
            var from = filters.DateFrom.Value.ToString("yyyy-MM-dd");
            whereClauses.Add("(\"TreatmentDate\" IS NOT NULL AND \"TreatmentDate\" <> '' AND \"TreatmentDate\" >= @dateFrom)");
            parameters.Add(("@dateFrom", from));
        }
        if (filters.DateTo.HasValue)
        {
            var to = filters.DateTo.Value.ToString("yyyy-MM-dd");
            whereClauses.Add("(\"TreatmentDate\" IS NOT NULL AND \"TreatmentDate\" <> '' AND \"TreatmentDate\" <= @dateTo)");
            parameters.Add(("@dateTo", to));
        }

        // Tab-specific record kind filter
        var tabFilter = activeTab switch
        {
            "Submissions" => "\"RecordKind\" = 'Submission'",
            "Resubmissions" => "\"RecordKind\" = 'Submission' AND \"ResubmissionType\" IS NOT NULL AND \"ResubmissionType\" <> ''",
            "Remittance" => "\"RecordKind\" = 'Remittance'",
            "Denials" => "\"RecordKind\" = 'Remittance' AND \"DenialCodesJson\" IS NOT NULL AND \"DenialCodesJson\" <> '' AND \"DenialCodesJson\" <> '[]'",
            _ => "1=1"
        };
        whereClauses.Add(tabFilter);

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // ── Aggregate KPI metrics from real data (single query) ──
        var kpiSql = @"
            SELECT
                COUNT(*) AS TotalClaims,
                SUM(""NetAmount"") AS NetTotal,
                SUM(""GrossAmount"") AS GrossTotal,
                SUM(""PaidAmount"") AS PaidTotal,
                SUM(CASE WHEN ""TreatmentDate"" IS NOT NULL AND ""TreatmentDate"" <> '' AND ""TreatmentDate"" >= @thirtyDaysAgo THEN 1 ELSE 0 END) AS CurrentPeriod,
                SUM(CASE WHEN ""TreatmentDate"" IS NOT NULL AND ""TreatmentDate"" <> '' AND ""TreatmentDate"" >= @sixtyDaysAgo AND ""TreatmentDate"" < @thirtyDaysAgo THEN 1 ELSE 0 END) AS PriorPeriod,
                SUM(CASE WHEN ""IsMatched"" THEN 1 ELSE 0 END) AS MatchedCount
            FROM ""XmlParsedRecords""
            " + whereSql;

        var now = DateTime.UtcNow;
        var thirtyDaysAgo = now.AddDays(-30).ToString("yyyy-MM-dd");
        var sixtyDaysAgo = now.AddDays(-60).ToString("yyyy-MM-dd");
        parameters.Add(("@thirtyDaysAgo", thirtyDaysAgo));
        parameters.Add(("@sixtyDaysAgo", sixtyDaysAgo));

        int totalClaims = 0;
        decimal netTotal = 0, grossTotal = 0, paidTotal = 0;
        int currentPeriod = 0, priorPeriod = 0, matchedCount = 0;

        using (var conn = _db.Database.GetDbConnection())
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = kpiSql;
            cmd.CommandTimeout = 300;
            foreach (var p in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value;
                cmd.Parameters.Add(param);
            }
            using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                totalClaims = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                netTotal = rdr.IsDBNull(1) ? 0 : rdr.GetDecimal(1);
                grossTotal = rdr.IsDBNull(2) ? 0 : rdr.GetDecimal(2);
                paidTotal = rdr.IsDBNull(3) ? 0 : rdr.GetDecimal(3);
                currentPeriod = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                priorPeriod = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                matchedCount = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);
            }
        }

        var claimDelta = priorPeriod > 0
            ? ((currentPeriod - priorPeriod) * 100.0 / priorPeriod)
            : 0;

        // Tab-specific metric 3 and 4
        var (metric3, metric4) = activeTab switch
        {
            "Submissions" or "Resubmissions" => (
                new DashboardMetric
                {
                    Label = "Gross Value",
                    Value = FormatAed(grossTotal),
                    Delta = "",
                    Icon = "fa-money-bill-wave",
                    Tone = "gold"
                },
                new DashboardMetric
                {
                    Label = "Avg per Claim",
                    Value = totalClaims > 0 ? FormatAed(netTotal / totalClaims) : "—",
                    Delta = "",
                    Icon = "fa-calculator",
                    Tone = "blue"
                }
            ),
            "Remittance" => (
                new DashboardMetric
                {
                    Label = "Paid Amount",
                    Value = FormatAed(paidTotal),
                    Delta = grossTotal > 0 ? $"{(paidTotal / grossTotal * 100):F1}% of gross" : "",
                    Icon = "fa-hand-holding-dollar",
                    Tone = "green"
                },
                new DashboardMetric
                {
                    Label = "Matched",
                    Value = $"{matchedCount:N0}",
                    Delta = totalClaims > 0 ? $"{(matchedCount * 100.0 / totalClaims):F0}%" : "",
                    Icon = "fa-link",
                    Tone = "blue"
                }
            ),
            "Denials" => (
                new DashboardMetric
                {
                    Label = "Denied Value",
                    Value = FormatAed(grossTotal - paidTotal),
                    Delta = "",
                    Icon = "fa-ban",
                    Tone = "red"
                },
                new DashboardMetric
                {
                    Label = "Denial Rate",
                    Value = grossTotal > 0 ? $"{((grossTotal - paidTotal) / grossTotal * 100):F1}%" : "—",
                    Delta = "",
                    Icon = "fa-chart-pie",
                    Tone = "orange"
                }
            ),
            _ => (
                new DashboardMetric
                {
                    Label = "Gross Value",
                    Value = FormatAed(grossTotal),
                    Delta = "",
                    Icon = "fa-coins",
                    Tone = "gold"
                },
                new DashboardMetric
                {
                    Label = "Paid Amount",
                    Value = FormatAed(paidTotal),
                    Delta = "",
                    Icon = "fa-hand-holding-dollar",
                    Tone = "green"
                }
            )
        };

        var metrics = new List<DashboardMetric>
        {
            new() { Label = "Total Claims", Value = $"{totalClaims:N0}", Delta = FormatDelta(claimDelta), Icon = "fa-file-medical", Tone = "teal" },
            new() { Label = "Net Value", Value = FormatAed(netTotal), Delta = "", Icon = "fa-coins", Tone = "gold" },
            metric3,
            metric4
        };

        // ── Trend: monthly claim counts over last 6 months (single SQL query) ──
        var trend = new List<DashboardTrendPoint>();
        var sixMonthsAgo = now.AddMonths(-6);
        var trendSql = @"
            SELECT ""ServiceYear"", ""ServiceMonth"", COUNT(*) AS Count
            FROM ""XmlParsedRecords""
            " + whereSql + @"
            AND ""ServiceYear"" IS NOT NULL AND ""ServiceMonth"" IS NOT NULL
            GROUP BY ""ServiceYear"", ""ServiceMonth""
            ORDER BY ""ServiceYear"", ""ServiceMonth""";

        var trendData = new List<(string Year, string Month, int Count)>();
        using (var conn = _db.Database.GetDbConnection())
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = trendSql;
            cmd.CommandTimeout = 300;
            foreach (var p in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value;
                cmd.Parameters.Add(param);
            }
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var year = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var month = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                var count = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
                trendData.Add((year, month, count));
            }
        }

        for (int i = 0; i < 6; i++)
        {
            var month = sixMonthsAgo.AddMonths(i + 1);
            var yr = month.Year.ToString();
            var mo = month.Month.ToString("D2");
            var count = trendData
                .Where(t => t.Year == yr && t.Month == mo)
                .Sum(t => t.Count);
            trend.Add(new DashboardTrendPoint
            {
                Label = month.ToString("MMM"),
                Value = count
            });
        }

        // ── Breakdown: top categories by tab (SQL for all tabs) ──
        var breakdown = await GetBreakdownByTabAsync(activeTab, whereClauses, parameters);

        // ── Insights: data-driven observations ──
        var insights = BuildInsights(activeTab, totalClaims, netTotal, paidTotal, grossTotal, claimDelta, breakdown);

        var summary = BuildSummary(activeTab, totalClaims, netTotal, paidTotal);

        return new RCMDashboardViewModel
        {
            ActiveTab = activeTab,
            Tabs = tabs,
            StableFieldTitle = stableFieldTitle,
            StableFieldDetail = stableFieldDetail,
            Summary = summary,
            RefreshedAt = DateTime.Now,
            Filters = filters,
            FacilityOptions = facilityOptions,
            ReceiverOptions = receiverOptions,
            PayerOptions = payerOptions,
            EncounterTypeOptions = encounterTypeOptions,
            Metrics = metrics,
            Trend = trend,
            Breakdown = breakdown,
            Insights = insights
        };
    }

    private static string FormatAed(decimal amount) =>
        amount >= 1_000_000 ? $"AED {amount / 1_000_000:F2}M"
        : amount >= 1_000 ? $"AED {amount / 1_000:F1}K"
        : $"AED {amount:F0}";

    private static string FormatDelta(double pct) =>
        pct == 0 ? "—" : pct > 0 ? $"+{pct:F1}%" : $"{pct:F1}%";

    private async Task<List<DashboardBreakdownItem>> BuildBreakdownByField(
        IQueryable<XmlParsedRecord> query,
        System.Linq.Expressions.Expression<Func<XmlParsedRecord, string>> fieldSelector)
    {
        return await query
            .GroupBy(fieldSelector)
            .Select(g => new DashboardBreakdownItem
            {
                Label = g.Key,
                Value = g.Count(),
                Detail = ""
            })
            .OrderByDescending(b => b.Value)
            .Take(6)
            .ToListAsync();
    }

    private async Task<List<DashboardBreakdownItem>> BuildBreakdownByDenialCategory(
        IQueryable<XmlParsedRecord> query)
    {
        var categories = await query
            .Where(r => r.ClaimCategory != null && r.ClaimCategory != "")
            .GroupBy(r => r.ClaimCategory!)
            .Select(g => new DashboardBreakdownItem
            {
                Label = g.Key,
                Value = g.Count(),
                Detail = ""
            })
            .OrderByDescending(b => b.Value)
            .Take(6)
            .ToListAsync();
        return categories.Count > 0 ? categories
            : new List<DashboardBreakdownItem> { new() { Label = "No denial data", Value = 0, Detail = "Upload remittance files to see denial categories" } };
    }

    private static List<DashboardInsight> BuildInsights(
        string tab, int totalClaims, decimal netTotal, decimal paidTotal,
        decimal grossTotal, double claimDelta, List<DashboardBreakdownItem> breakdown)
    {
        var insights = new List<DashboardInsight>();

        if (totalClaims == 0)
        {
            insights.Add(new DashboardInsight
            {
                Title = "No data yet",
                Detail = "Upload XML files via Portal Sync to populate this dashboard.",
                Status = "Action"
            });
            return insights;
        }

        if (claimDelta > 10)
            insights.Add(new DashboardInsight { Title = "Volume surge", Detail = $"Claims up {claimDelta:F0}% vs prior 30 days — verify capacity.", Status = "Watch" });
        else if (claimDelta < -10)
            insights.Add(new DashboardInsight { Title = "Volume drop", Detail = $"Claims down {Math.Abs(claimDelta):F0}% vs prior 30 days.", Status = "Watch" });
        else
            insights.Add(new DashboardInsight { Title = "Stable volume", Detail = "Claim volume is within normal range vs prior period.", Status = "Good" });

        if (grossTotal > 0 && tab is "Remittance" or "Denials")
        {
            var recoveryPct = paidTotal / grossTotal * 100;
            insights.Add(new DashboardInsight
            {
                Title = "Recovery rate",
                Detail = $"{recoveryPct:F1}% of gross amount recovered.",
                Status = recoveryPct >= 80 ? "Good" : recoveryPct >= 60 ? "Watch" : "Action"
            });
        }

        var top = breakdown.FirstOrDefault();
        if (top != null && top.Value > 0)
        {
            insights.Add(new DashboardInsight
            {
                Title = "Top category",
                Detail = $"\"{top.Label}\" leads with {top.Value:N0} records.",
                Status = "Stable"
            });
        }

        return insights;
    }

    private static string BuildSummary(string tab, int totalClaims, decimal netTotal, decimal paidTotal)
    {
        if (totalClaims == 0) return $"No {tab.ToLower()} data available. Sync portal data to populate this view.";
        return tab switch
        {
            "Submissions" => $"{totalClaims:N0} submissions totaling {FormatAed(netTotal)} net value.",
            "Resubmissions" => $"{totalClaims:N0} resubmissions in the current dataset.",
            "Remittance" => $"{totalClaims:N0} remittance records with {FormatAed(paidTotal)} paid of {FormatAed(netTotal)} net.",
            "Denials" => $"{totalClaims:N0} denied claims identified across remittance data.",
            "Clinicians" => $"{totalClaims:N0} records across all clinicians.",
            "Operations" => $"{totalClaims:N0} operational records for throughput analysis.",
            "Insurance" => $"{totalClaims:N0} records across payer networks.",
            "Department" => $"{totalClaims:N0} records across departments.",
            _ => $"{totalClaims:N0} records loaded."
        };
    }

    private async Task<List<DashboardBreakdownItem>> GetCachedSubmissionsBreakdownAsync(
        List<string> whereClauses, List<(string Name, object Value)> parameters)
    {
        var cacheKey = "dashboard:submissions:breakdown:v1";
        if (_cache.TryGetValue<List<DashboardBreakdownItem>>(cacheKey, out var cached))
            return cached;

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        var sql = @"
            SELECT COALESCE(""EncounterType"", 'Unknown') AS Category, COUNT(*) AS Count
            FROM ""XmlParsedRecords""
            " + whereSql + @"
            GROUP BY COALESCE(""EncounterType"", 'Unknown')
            ORDER BY Count DESC
            LIMIT 6";

        var list = new List<DashboardBreakdownItem>();
        using (var conn = _db.Database.GetDbConnection())
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            foreach (var p in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value;
                cmd.Parameters.Add(param);
            }
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var label = rdr.IsDBNull(0) ? "Unknown" : rdr.GetString(0);
                var value = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                list.Add(new DashboardBreakdownItem { Label = label, Value = value, Detail = "" });
            }
        }

        if (list.Count == 0)
            list.Add(new DashboardBreakdownItem { Label = "No submission data", Value = 0, Detail = "Upload XML files via Portal Sync" });

        var cacheEntry = _cache.CreateEntry(cacheKey);
        cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
        cacheEntry.SetValue(list);
        cacheEntry.Dispose();

        return list;
    }

    private async Task<List<DashboardBreakdownItem>> GetBreakdownByTabAsync(
        string activeTab, List<string> whereClauses, List<(string Name, object Value)> parameters)
    {
        var cacheKey = $"dashboard:{activeTab.ToLower()}:breakdown:v1";
        if (_cache.TryGetValue<List<DashboardBreakdownItem>>(cacheKey, out var cached))
            return cached;

        string fieldSql;
        string emptyMsg;
        switch (activeTab)
        {
            case "Submissions":
                fieldSql = "COALESCE(\"EncounterType\", 'Unknown')";
                emptyMsg = "Upload XML files via Portal Sync";
                break;
            case "Resubmissions":
                fieldSql = "COALESCE(\"ResubmissionType\", 'Unknown')";
                emptyMsg = "No resubmission data available";
                break;
            case "Remittance":
                fieldSql = "COALESCE(\"PayerName\", \"PayerId\", 'Unknown')";
                emptyMsg = "No remittance data available";
                break;
            case "Denials":
                fieldSql = "COALESCE(\"ClaimCategory\", 'Unknown')";
                emptyMsg = "Upload remittance files to see denial categories";
                break;
            case "Clinicians":
                fieldSql = "COALESCE(\"Clinician\", 'Unknown')";
                emptyMsg = "No clinician data available";
                break;
            case "Operations":
                fieldSql = "COALESCE(\"EncounterType\", 'Unknown')";
                emptyMsg = "No operational data available";
                break;
            case "Insurance":
                fieldSql = "COALESCE(\"PayerName\", \"PayerId\", 'Unknown')";
                emptyMsg = "No payer data available";
                break;
            case "Department":
                fieldSql = "COALESCE(\"EncounterType\", 'Unknown')";
                emptyMsg = "No department data available";
                break;
            default:
                fieldSql = "COALESCE(\"EncounterType\", 'Unknown')";
                emptyMsg = "Upload XML files via Portal Sync";
                break;
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        var sql = @"
            SELECT " + fieldSql + @" AS Category, COUNT(*) AS Count
            FROM ""XmlParsedRecords""
            " + whereSql + @"
            GROUP BY " + fieldSql + @"
            ORDER BY Count DESC
            LIMIT 6";

        var list = new List<DashboardBreakdownItem>();
        using (var conn = _db.Database.GetDbConnection())
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            foreach (var p in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value;
                cmd.Parameters.Add(param);
            }
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var label = rdr.IsDBNull(0) ? "Unknown" : rdr.GetString(0);
                var value = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                list.Add(new DashboardBreakdownItem { Label = label, Value = value, Detail = "" });
            }
        }

        if (list.Count == 0)
        {
            list.Add(new DashboardBreakdownItem { Label = "No data", Value = 0, Detail = emptyMsg });
        }

        var cacheEntry = _cache.CreateEntry(cacheKey);
        cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
        cacheEntry.SetValue(list);
        cacheEntry.Dispose();

        return list;
    }
}
