namespace Analytika.Services;

/// <summary>
/// Compares the totals extracted from a generated report with the prepared XML
/// cache and, when supplied, a fresh DHPO SearchTransactions result.  This class
/// deliberately does not connect to DHPO: callers continue to use the existing
/// PortalCredentials -> portal service flow and pass the resulting totals here.
/// </summary>
public interface IReportValidationService
{
    ReportValidationResult Validate(
        ReportValidationSnapshot report,
        ReportValidationSnapshot parsedDatabase,
        ReportValidationSnapshot? directDhpo = null,
        ReportValidationTolerances? tolerances = null);
}

public sealed class ReportValidationService : IReportValidationService
{
    public ReportValidationResult Validate(
        ReportValidationSnapshot report,
        ReportValidationSnapshot parsedDatabase,
        ReportValidationSnapshot? directDhpo = null,
        ReportValidationTolerances? tolerances = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(parsedDatabase);
        tolerances ??= ReportValidationTolerances.Default;

        var checks = new List<ReportValidationCheck>();
        CompareScope(report, parsedDatabase, "Parsed DB", checks);
        CompareSnapshot(report, parsedDatabase, "Parsed DB", tolerances, checks);

        if (directDhpo is not null)
        {
            CompareScope(report, directDhpo, "Direct DHPO", checks);
            CompareSnapshot(report, directDhpo, "Direct DHPO", tolerances, checks);
        }

        AddDuplicateCheck(report, "Generated report", checks);
        AddDuplicateCheck(parsedDatabase, "Parsed DB", checks);
        if (directDhpo is not null) AddDuplicateCheck(directDhpo, "Direct DHPO", checks);

        var status = checks.Any(c => c.Status == ReportValidationStatus.Fail)
            ? ReportValidationStatus.Fail
            : checks.Any(c => c.Status == ReportValidationStatus.Warn)
                ? ReportValidationStatus.Warn
                : ReportValidationStatus.Pass;

        return new ReportValidationResult(status, checks, directDhpo is not null);
    }

    private static void CompareScope(
        ReportValidationSnapshot actual,
        ReportValidationSnapshot expected,
        string source,
        ICollection<ReportValidationCheck> checks)
    {
        var dateMatch = actual.DateFrom.Date == expected.DateFrom.Date &&
                        actual.DateTo.Date == expected.DateTo.Date;
        checks.Add(new("Date scope", source, dateMatch ? ReportValidationStatus.Pass : ReportValidationStatus.Fail,
            $"report={actual.DateFrom:yyyy-MM-dd}..{actual.DateTo:yyyy-MM-dd}; source={expected.DateFrom:yyyy-MM-dd}..{expected.DateTo:yyyy-MM-dd}"));

        var reportFacilities = NormalizeFacilities(actual.FacilityIds);
        var expectedFacilities = NormalizeFacilities(expected.FacilityIds);
        var facilityMatch = reportFacilities.SetEquals(expectedFacilities);
        checks.Add(new("Facility scope", source, facilityMatch ? ReportValidationStatus.Pass : ReportValidationStatus.Fail,
            $"report=[{string.Join(',', reportFacilities)}]; source=[{string.Join(',', expectedFacilities)}]"));
    }

    private static HashSet<int> NormalizeFacilities(IReadOnlyCollection<int>? ids) =>
        ids is null ? [] : ids.ToHashSet();

    private static void CompareSnapshot(
        ReportValidationSnapshot report,
        ReportValidationSnapshot expected,
        string source,
        ReportValidationTolerances tolerance,
        ICollection<ReportValidationCheck> checks)
    {
        Compare("Distinct claims", report.DistinctClaimCount, expected.DistinctClaimCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
        Compare("Report rows", report.RowCount, expected.RowCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
        Compare("Activities", report.ActivityCount, expected.ActivityCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
        Compare("Submitted/net amount", report.SubmittedNetAmount, expected.SubmittedNetAmount, source, tolerance.AmountWarning, tolerance.AmountFailure, checks);
        Compare("Paid amount", report.PaidAmount, expected.PaidAmount, source, tolerance.AmountWarning, tolerance.AmountFailure, checks);
        Compare("Remittances", report.RemittanceCount, expected.RemittanceCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
        Compare("Files", report.FileCount, expected.FileCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
        Compare("Transactions", report.TransactionCount, expected.TransactionCount, source, tolerance.CountWarning, tolerance.CountFailure, checks);
    }

    private static void Compare(
        string metric,
        decimal? actual,
        decimal? expected,
        string source,
        decimal warningTolerance,
        decimal failureTolerance,
        ICollection<ReportValidationCheck> checks)
    {
        if (actual is null || expected is null)
        {
            checks.Add(new(metric, source, ReportValidationStatus.Warn,
                $"not comparable: report={Display(actual)}; source={Display(expected)}"));
            return;
        }

        var delta = Math.Abs(actual.Value - expected.Value);
        var status = delta <= warningTolerance
            ? ReportValidationStatus.Pass
            : delta <= failureTolerance ? ReportValidationStatus.Warn : ReportValidationStatus.Fail;
        checks.Add(new(metric, source, status,
            $"report={actual.Value:0.##}; source={expected.Value:0.##}; delta={delta:0.##}"));
    }

    private static string Display(decimal? value) => value?.ToString("0.##") ?? "unavailable";

    private static void AddDuplicateCheck(
        ReportValidationSnapshot snapshot,
        string source,
        ICollection<ReportValidationCheck> checks)
    {
        if (snapshot.DuplicateCount is null)
        {
            checks.Add(new("Duplicates", source, ReportValidationStatus.Warn, "duplicate count unavailable"));
            return;
        }

        checks.Add(new("Duplicates", source,
            snapshot.DuplicateCount == 0 ? ReportValidationStatus.Pass : ReportValidationStatus.Fail,
            $"duplicates={snapshot.DuplicateCount}"));
    }
}

public sealed record ReportValidationSnapshot(
    DateTime DateFrom,
    DateTime DateTo,
    IReadOnlyCollection<int>? FacilityIds,
    long? DistinctClaimCount,
    long? RowCount,
    long? ActivityCount,
    decimal? SubmittedNetAmount,
    decimal? PaidAmount,
    long? RemittanceCount,
    long? FileCount,
    long? TransactionCount,
    long? DuplicateCount);

public sealed record ReportValidationTolerances(
    decimal CountWarning,
    decimal CountFailure,
    decimal AmountWarning,
    decimal AmountFailure)
{
    public static ReportValidationTolerances Default { get; } = new(0, 1, 0.01m, 1.00m);
}

public sealed record ReportValidationResult(
    ReportValidationStatus Status,
    IReadOnlyList<ReportValidationCheck> Checks,
    bool DirectDhpoCompared)
{
    public int Passed => Checks.Count(c => c.Status == ReportValidationStatus.Pass);
    public int Warnings => Checks.Count(c => c.Status == ReportValidationStatus.Warn);
    public int Failed => Checks.Count(c => c.Status == ReportValidationStatus.Fail);
}

public sealed record ReportValidationCheck(
    string Metric,
    string Source,
    ReportValidationStatus Status,
    string Evidence);

public enum ReportValidationStatus { Pass, Warn, Fail }
