using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
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
                    FacilityName = portal.Length > 0 ? $"{f.Name} {portal}" : f.Name,
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

        var recordsQuery = _db.XmlParsedRecords
            .AsNoTracking()
            .Where(r => r.ReadyForReport);

        recordsQuery = activeTab switch
        {
            "Submissions" => recordsQuery.Where(r => r.RecordKind == "Submission"),
            "Resubmissions" => recordsQuery.Where(r => r.ResubmissionType != null && r.ResubmissionType != ""),
            "Remittance" => recordsQuery.Where(r => r.RecordKind == "Remittance"),
            "Denials" => recordsQuery.Where(r => r.DenialCodesJson != null && r.DenialCodesJson != ""),
            "Clinicians" => recordsQuery.Where(r => r.Clinician != null && r.Clinician != ""),
            _ => recordsQuery
        };

        if (filters.FacilityId.HasValue)
            recordsQuery = recordsQuery.Where(r => r.FacilityId == filters.FacilityId.Value);

        if (!string.IsNullOrWhiteSpace(filters.Receiver))
            recordsQuery = recordsQuery.Where(r => (r.ReceiverName ?? r.ReceiverId) == filters.Receiver);

        if (!string.IsNullOrWhiteSpace(filters.Payer))
            recordsQuery = recordsQuery.Where(r => (r.PayerName ?? r.PayerId) == filters.Payer);

        if (!string.IsNullOrWhiteSpace(filters.EncounterType))
            recordsQuery = recordsQuery.Where(r => r.EncounterType == filters.EncounterType);

        var datedRecords = (await recordsQuery.ToListAsync())
            .Select(r => new { Record = r, Date = DashboardDate(r) })
            .Where(r => (!filters.DateFrom.HasValue || (r.Date.HasValue && r.Date.Value >= filters.DateFrom.Value)) &&
                        (!filters.DateTo.HasValue || (r.Date.HasValue && r.Date.Value <= filters.DateTo.Value)))
            .ToList();

        var records = datedRecords.Select(r => r.Record).ToList();
        var claimIds = records
            .Select(r => string.IsNullOrWhiteSpace(r.ClaimId) ? $"record:{r.Id}" : r.ClaimId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var distinctClaimCount = claimIds.Count;
        var submissionCount = records.Count(r => r.RecordKind == "Submission");
        var remittanceCount = records.Count(r => r.RecordKind == "Remittance");
        var deniedRecordCount = records.Count(HasDenial);
        var deniedClaimCount = records
            .Where(HasDenial)
            .Select(r => string.IsNullOrWhiteSpace(r.ClaimId) ? $"record:{r.Id}" : r.ClaimId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var matchedCount = records.Count(r => r.IsMatched);
        var unmatchedCount = Math.Max(0, distinctClaimCount - matchedCount);
        var submittedAmount = records.Where(r => r.RecordKind == "Submission").Sum(r => r.NetAmount);
        var paidAmount = records.Where(r => r.RecordKind == "Remittance").Sum(r => r.PaidAmount);
        var netAmount = submittedAmount > 0 ? submittedAmount : records.Sum(r => r.NetAmount);
        var cleanRate = distinctClaimCount == 0
            ? 0
            : (int)Math.Round((distinctClaimCount - deniedClaimCount) * 100.0 / distinctClaimCount);
        var tatDays = AverageTatDays(records);

        var trend = datedRecords
            .Where(r => r.Date.HasValue)
            .GroupBy(r => new DateOnly(r.Date!.Value.Year, r.Date.Value.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => new DashboardTrendPoint
            {
                Label = g.Key.ToString("MMM yy", CultureInfo.InvariantCulture),
                Value = g.Select(x => string.IsNullOrWhiteSpace(x.Record.ClaimId) ? $"record:{x.Record.Id}" : x.Record.ClaimId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
            })
            .TakeLast(12)
            .ToList();

        if (activeTab.Equals("Remittance", StringComparison.OrdinalIgnoreCase))
        {
            trend = datedRecords
                .Where(r => r.Date.HasValue)
                .GroupBy(r => new DateOnly(r.Date!.Value.Year, r.Date.Value.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g => new DashboardTrendPoint
                {
                    Label = g.Key.ToString("MMM yy", CultureInfo.InvariantCulture),
                    Value = (int)Math.Round(g.Sum(x => x.Record.PaidAmount))
                })
                .TakeLast(12)
                .ToList();
        }
        else if (activeTab.Equals("Denials", StringComparison.OrdinalIgnoreCase))
        {
            trend = datedRecords
                .Where(r => r.Date.HasValue)
                .GroupBy(r => new DateOnly(r.Date!.Value.Year, r.Date.Value.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g => new DashboardTrendPoint
                {
                    Label = g.Key.ToString("MMM yy", CultureInfo.InvariantCulture),
                    Value = g.Count(x => HasDenial(x.Record))
                })
                .TakeLast(12)
                .ToList();
        }

        var breakdown = BuildBreakdown(activeTab, records);
        var pendingFiles = await CountPendingFilesAsync(filters);
        var latestDate = datedRecords
            .Where(r => r.Date.HasValue)
            .Select(r => r.Date!.Value)
            .DefaultIfEmpty()
            .Max();
        var hasLatestDate = latestDate != default;
        var scopeText = BuildScopeText(filters, records.Count);
        var summary = records.Count == 0
            ? "No parsed portal records match the selected filters."
            : BuildSummary(activeTab, records.Count, distinctClaimCount, submissionCount, remittanceCount, deniedClaimCount, paidAmount, netAmount, scopeText);

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
            Metrics = BuildMetrics(activeTab, distinctClaimCount, submissionCount, remittanceCount, deniedClaimCount, matchedCount, unmatchedCount, submittedAmount, paidAmount, netAmount, cleanRate, tatDays),
            Trend = trend,
            Breakdown = breakdown,
            Insights =
            [
                records.Count == 0
                    ? new DashboardInsight { Title = "No matching data", Detail = "Fetch and parse portal XML records for this filter scope.", Status = "Empty" }
                    : new DashboardInsight { Title = "Current scope", Detail = $"{records.Count:N0} parsed records are included in active dashboard calculation.", Status = "Actual" },
                pendingFiles > 0
                    ? new DashboardInsight { Title = "Pending files", Detail = $"{pendingFiles:N0} portal transaction files are still not downloaded.", Status = "Action" }
                    : new DashboardInsight { Title = "Downloaded files", Detail = "No pending portal transaction downloads were found for this facility scope.", Status = "Clear" },
                hasLatestDate
                    ? new DashboardInsight { Title = "Latest activity", Detail = $"Latest parsed reporting date is {latestDate:dd MMM yyyy}.", Status = "Fresh" }
                    : new DashboardInsight { Title = "Latest activity", Detail = "No reportable dates were found in the selected records.", Status = "Empty" }
            ]
        };
    }

    private async Task<int> CountPendingFilesAsync(RcmDashboardFilters filters)
    {
        var query = _db.PortalTransactions.AsNoTracking().Where(t => !t.FileDownloaded);
        if (filters.FacilityId.HasValue)
            query = query.Where(t => t.FacilityId == filters.FacilityId.Value);

        return await query.CountAsync();
    }

    private static List<DashboardBreakdownItem> BuildBreakdown(string activeTab, List<XmlParsedRecord> records)
    {
        IEnumerable<IGrouping<string, XmlParsedRecord>> groups = activeTab switch
        {
            "Denials" => records
                .SelectMany(r => DenialCodes(r).DefaultIfEmpty("Uncoded").Select(code => new { Code = code, Record = r }))
                .GroupBy(x => x.Code, x => x.Record),
            "Clinicians" => records.GroupBy(r => CleanGroup(r.Clinician, "Unassigned clinician")),
            "Insurance" or "Remittance" => records.GroupBy(r => CleanGroup(r.PayerName ?? r.PayerId, "Unassigned payer")),
            "Department" or "Operations" => records.GroupBy(r => CleanGroup(r.EncounterType, "Unspecified encounter")),
            "Resubmissions" => records.GroupBy(r => CleanGroup(r.ResubmissionType, "Unspecified resubmission")),
            _ => records.GroupBy(r => CleanGroup(r.EncounterType ?? r.ReceiverName ?? r.ReceiverId, "Unspecified"))
        };

        return groups
            .Select(g => new DashboardBreakdownItem
            {
                Label = g.Key,
                Value = g.Select(r => string.IsNullOrWhiteSpace(r.ClaimId) ? $"record:{r.Id}" : r.ClaimId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                Detail = $"{g.Count():N0} parsed records"
            })
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Label)
            .Take(8)
            .ToList();
    }

    private static string CleanGroup(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool HasDenial(XmlParsedRecord record)
        => DenialCodes(record).Count > 0;

    private static List<string> DenialCodes(XmlParsedRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DenialCodesJson))
            return [];

        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(record.DenialCodesJson);
            if (codes != null)
                return codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            // Some legacy imports stored denial codes as delimited text instead of JSON.
        }

        return record.DenialCodesJson
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateOnly? DashboardDate(XmlParsedRecord record)
        => TryDate(record.TreatmentDate)
            ?? TryDate(record.SubmissionDate)
            ?? TryDate(record.TransactionDate)
            ?? TryDate(record.SettlementDate)
            ?? ServiceMonthDate(record)
            ?? DateOnly.FromDateTime(record.ParsedAt);

    private static DateOnly? ServiceMonthDate(XmlParsedRecord record)
    {
        if (int.TryParse(record.ServiceYear, out var year) &&
            int.TryParse(record.ServiceMonth, out var month) &&
            year is >= 1900 and <= 2200 &&
            month is >= 1 and <= 12)
        {
            return new DateOnly(year, month, 1);
        }

        return null;
    }

    private static DateOnly? TryDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss",
            "dd/MM/yyyy",
            "dd/MM/yyyy HH:mm:ss",
            "MM/dd/yyyy",
            "MM/dd/yyyy HH:mm:ss"
        };

        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            return DateOnly.FromDateTime(exact);

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return DateOnly.FromDateTime(parsed);

        return null;
    }

    private static int? AverageTatDays(List<XmlParsedRecord> records)
    {
        var values = records
            .Select(r =>
            {
                var start = TryDate(r.TreatmentDate) ?? TryDate(r.SubmissionDate) ?? TryDate(r.TransactionDate);
                var end = TryDate(r.SettlementDate) ?? (r.MatchedAt.HasValue ? DateOnly.FromDateTime(r.MatchedAt.Value) : null);
                return start.HasValue && end.HasValue && end.Value >= start.Value
                    ? end.Value.DayNumber - start.Value.DayNumber
                    : (int?)null;
            })
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        return values.Count == 0 ? null : (int)Math.Round(values.Average());
    }

    private static string FormatMoney(decimal amount)
    {
        var abs = Math.Abs(amount);
        return abs switch
        {
            >= 1_000_000 => $"AED {amount / 1_000_000m:N1}M",
            >= 1_000 => $"AED {amount / 1_000m:N0}K",
            _ => $"AED {amount:N0}"
        };
    }

    private static string BuildScopeText(RcmDashboardFilters filters, int recordCount)
    {
        var parts = new List<string>();
        if (filters.FacilityId.HasValue) parts.Add($"facility #{filters.FacilityId.Value}");
        if (!string.IsNullOrWhiteSpace(filters.Receiver)) parts.Add($"receiver {filters.Receiver}");
        if (!string.IsNullOrWhiteSpace(filters.Payer)) parts.Add($"payer {filters.Payer}");
        if (!string.IsNullOrWhiteSpace(filters.EncounterType)) parts.Add($"encounter {filters.EncounterType}");
        if (filters.DateFrom.HasValue || filters.DateTo.HasValue)
            parts.Add($"dates {(filters.DateFrom?.ToString("dd/MM/yyyy") ?? "start")} to {(filters.DateTo?.ToString("dd/MM/yyyy") ?? "today")}");

        return parts.Count == 0
            ? "No filters are applied."
            : $"Applied filters: {string.Join(", ", parts)}.";
    }

    private static string BuildSummary(
        string activeTab,
        int recordCount,
        int distinctClaimCount,
        int submissionCount,
        int remittanceCount,
        int deniedClaimCount,
        decimal paidAmount,
        decimal netAmount,
        string scopeText)
    {
        return activeTab switch
        {
            "Submissions" => $"{distinctClaimCount:N0} claims from {submissionCount:N0} submission rows. {scopeText}",
            "Resubmissions" => $"{recordCount:N0} parsed resubmission rows across {distinctClaimCount:N0} claims. {scopeText}",
            "Remittance" => $"{remittanceCount:N0} remittance rows, {FormatMoney(paidAmount)} paid value. {scopeText}",
            "Denials" => $"{deniedClaimCount:N0} denied claims surfaced from {recordCount:N0} parsed rows. {scopeText}",
            "Clinicians" => $"{distinctClaimCount:N0} claims grouped by clinician across {recordCount:N0} rows. {scopeText}",
            "Operations" => $"{recordCount:N0} operational records analyzed; net value {FormatMoney(netAmount)}. {scopeText}",
            "Insurance" => $"{distinctClaimCount:N0} claims analyzed for payer behavior. {scopeText}",
            "Department" => $"{recordCount:N0} records grouped for department view. {scopeText}",
            _ => $"{activeTab} is calculated from {recordCount:N0} parsed records and {distinctClaimCount:N0} distinct claims. {scopeText}"
        };
    }

    private static List<DashboardMetric> BuildMetrics(
        string activeTab,
        int distinctClaimCount,
        int submissionCount,
        int remittanceCount,
        int deniedClaimCount,
        int matchedCount,
        int unmatchedCount,
        decimal submittedAmount,
        decimal paidAmount,
        decimal netAmount,
        int cleanRate,
        int? tatDays)
    {
        return activeTab switch
        {
            "Remittance" => new List<DashboardMetric>
            {
                new() { Label = "Remit Rows", Value = $"{remittanceCount:N0}", Delta = "Parsed RA rows", Icon = "fa-receipt", Tone = "teal" },
                new() { Label = "Paid Value", Value = FormatMoney(paidAmount), Delta = "Paid amount", Icon = "fa-sack-dollar", Tone = "gold" },
                new() { Label = "Matched Claims", Value = $"{matchedCount:N0}", Delta = $"{unmatchedCount:N0} unmatched", Icon = "fa-link", Tone = "green" },
                new() { Label = "TAT", Value = tatDays.HasValue ? $"{tatDays.Value:N0} days" : "N/A", Delta = "Settlement dates", Icon = "fa-clock", Tone = "blue" }
            },
            "Denials" => new List<DashboardMetric>
            {
                new() { Label = "Denied Claims", Value = $"{deniedClaimCount:N0}", Delta = "Claims with denial", Icon = "fa-ban", Tone = "teal" },
                new() { Label = "Claim Count", Value = $"{distinctClaimCount:N0}", Delta = "Distinct claims", Icon = "fa-file-medical", Tone = "gold" },
                new() { Label = "Clean Rate", Value = $"{cleanRate}%", Delta = "Inverse of denial share", Icon = "fa-circle-check", Tone = "green" },
                new() { Label = "TAT", Value = tatDays.HasValue ? $"{tatDays.Value:N0} days" : "N/A", Delta = "Actual dates", Icon = "fa-clock", Tone = "blue" }
            },
            _ => new List<DashboardMetric>
            {
                new() { Label = "Total Claims", Value = $"{distinctClaimCount:N0}", Delta = activeTab == "Submissions" ? $"{submissionCount:N0} submission rows" : "Parsed claims", Icon = "fa-file-medical", Tone = "teal" },
                new() { Label = "Net Value", Value = FormatMoney(netAmount), Delta = activeTab == "Submissions" ? "Submission net sum" : "Claim net sum", Icon = "fa-coins", Tone = "gold" },
                new() { Label = "Clean Rate", Value = $"{cleanRate}%", Delta = $"{deniedClaimCount:N0} denied", Icon = "fa-circle-check", Tone = "green" },
                new() { Label = "TAT", Value = tatDays.HasValue ? $"{tatDays.Value:N0} days" : "N/A", Delta = "Actual dates", Icon = "fa-clock", Tone = "blue" }
            }
        };
    }
}
