using Analytika.Services;
using FluentAssertions;
using Xunit;

namespace Analytika.Tests.Services;

public class ReportValidationServiceTests
{
    private readonly ReportValidationService _validator = new();

    [Fact]
    public void Identical_report_database_and_dhpo_snapshots_pass()
    {
        var snapshot = Snapshot();

        var result = _validator.Validate(snapshot, snapshot, snapshot);

        result.Status.Should().Be(ReportValidationStatus.Pass);
        result.DirectDhpoCompared.Should().BeTrue();
        result.Failed.Should().Be(0);
        result.Checks.Should().Contain(c => c.Metric == "Distinct claims" && c.Source == "Direct DHPO");
    }

    [Fact]
    public void Small_amount_difference_warns_but_larger_difference_fails()
    {
        var report = Snapshot(paid: 900.50m);

        var warning = _validator.Validate(report, Snapshot(paid: 900m));
        var failure = _validator.Validate(report, Snapshot(paid: 890m));

        warning.Status.Should().Be(ReportValidationStatus.Warn);
        warning.Checks.Should().ContainSingle(c => c.Metric == "Paid amount" && c.Status == ReportValidationStatus.Warn);
        failure.Status.Should().Be(ReportValidationStatus.Fail);
        failure.Checks.Should().ContainSingle(c => c.Metric == "Paid amount" && c.Status == ReportValidationStatus.Fail);
    }

    [Fact]
    public void Scope_mismatch_and_duplicates_fail()
    {
        var report = Snapshot(facilities: [2], duplicates: 1);

        var result = _validator.Validate(report, Snapshot(facilities: [1]));

        result.Status.Should().Be(ReportValidationStatus.Fail);
        result.Checks.Should().Contain(c => c.Metric == "Facility scope" && c.Status == ReportValidationStatus.Fail);
        result.Checks.Should().Contain(c => c.Metric == "Duplicates" && c.Source == "Generated report" && c.Status == ReportValidationStatus.Fail);
    }

    [Fact]
    public void Missing_optional_metric_is_a_warning_with_evidence()
    {
        var report = Snapshot() with { ActivityCount = null };

        var result = _validator.Validate(report, Snapshot());

        result.Status.Should().Be(ReportValidationStatus.Warn);
        result.Checks.Should().ContainSingle(c => c.Metric == "Activities" &&
                                                   c.Status == ReportValidationStatus.Warn &&
                                                   c.Evidence.Contains("unavailable"));
    }

    private static ReportValidationSnapshot Snapshot(
        decimal paid = 900m,
        int[]? facilities = null,
        long duplicates = 0) => new(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), facilities ?? [1],
            10, 12, 30, 1000m, paid, 12, 2, 2, duplicates);
}
