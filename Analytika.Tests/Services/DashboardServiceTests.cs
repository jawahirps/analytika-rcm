using Xunit;
using System.Reflection;
using Analytika.Services;

namespace Analytika.Tests.Services;

public class DashboardServiceTests
{
    [Theory]
    [InlineData(0, "AED 0")]
    [InlineData(500, "AED 500")]
    [InlineData(1500, "AED 1.5K")]
    [InlineData(1_500_000, "AED 1.50M")]
    public void FormatAed_FormatsCorrectly(decimal input, string expected)
    {
        var method = typeof(DashboardService).GetMethod("FormatAed", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (string)method!.Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(10.5, "+10.5%")]
    [InlineData(-5.3, "-5.3%")]
    public void FormatDelta_FormatsCorrectly(double input, string expected)
    {
        var method = typeof(DashboardService).GetMethod("FormatDelta", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (string)method!.Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }
}
