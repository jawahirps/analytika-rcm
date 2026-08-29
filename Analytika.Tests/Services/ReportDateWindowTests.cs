using Analytika.Services;
using FluentAssertions;
using Xunit;

namespace Analytika.Tests.Services;

/// <summary>
/// Regression tests for the Remittance Activity "empty report" root cause:
/// dd/MM/yyyy form dates were culture-mis-bound (null or day/month transposed),
/// silently falling back to a last-month window with no data. The server-side
/// resolver must be authoritative and culture-proof.
/// </summary>
public class ReportDateWindowTests
{
    private static readonly DateTime Today = new(2026, 7, 21);

    [Fact]
    public void LastYear_ResolvesToFullPreviousCalendarYear()
    {
        ReportDateWindow.TryResolve("LastYear", null, null, Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2025, 1, 1));
        to.Should().Be(new DateTime(2025, 12, 31));
    }

    [Fact]
    public void ThisYear_ResolvesToCurrentCalendarYear()
    {
        ReportDateWindow.TryResolve("ThisYear", null, null, Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2026, 1, 1));
        to.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public void LastMonth_ResolvesToFullPreviousMonth()
    {
        ReportDateWindow.TryResolve("LastMonth", null, null, Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2026, 6, 1));
        to.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void Presets_IgnoreRawInputsEntirely()
    {
        // Garbage raw values must not affect preset resolution.
        ReportDateWindow.TryResolve("ThisMonth", "banana", "31/31/9999", Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2026, 7, 1));
        to.Should().Be(new DateTime(2026, 7, 31));
    }

    [Fact]
    public void Custom_ParsesDdMmYyyy_TheFormatTheUiActuallyPosts()
    {
        // "31/12/2025" broke invariant-culture binding (MM/dd) — the exact bug.
        ReportDateWindow.TryResolve("Custom", "01/07/2025", "31/12/2025", Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2025, 7, 1), "day/month must not be transposed");
        to.Should().Be(new DateTime(2025, 12, 31));
    }

    [Fact]
    public void Custom_AcceptsIsoDates()
    {
        ReportDateWindow.TryResolve("Custom", "2025-01-01", "2025-12-31", Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2025, 1, 1));
        to.Should().Be(new DateTime(2025, 12, 31));
    }

    [Fact]
    public void Custom_SwapsInvertedRange()
    {
        ReportDateWindow.TryResolve("Custom", "31/12/2025", "01/01/2025", Today, out var from, out var to)
            .Should().BeTrue();
        from.Should().Be(new DateTime(2025, 1, 1));
        to.Should().Be(new DateTime(2025, 12, 31));
    }

    [Theory]
    [InlineData("Custom", null, null)]
    [InlineData("Custom", "01/07/2025", null)]
    [InlineData("Custom", "not-a-date", "31/12/2025")]
    [InlineData("", null, null)]
    public void Custom_RejectsMissingOrInvalidDates_InsteadOfSilentFallback(
        string range, string? rawFrom, string? rawTo)
    {
        ReportDateWindow.TryResolve(range, rawFrom, rawTo, Today, out _, out _)
            .Should().BeFalse("invalid input must be rejected, never silently mis-windowed");
    }
}
