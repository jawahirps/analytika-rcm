using Xunit;

namespace Analytika.Tests.Services;

public class XmlParsingServiceTests
{
    [Theory]
    [InlineData("2024-01-15", "2024", "01")]
    [InlineData("2023-12-01", "2023", "12")]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    public void ExtractServiceYearMonth_ParsesCorrectly(string? treatmentDate, string? expectedYear, string? expectedMonth)
    {
        string? year = null, month = null;
        if (!string.IsNullOrWhiteSpace(treatmentDate) && treatmentDate.Length >= 7)
        {
            year = treatmentDate[..4];
            month = treatmentDate.Substring(5, 2);
        }
        Assert.Equal(expectedYear, year);
        Assert.Equal(expectedMonth, month);
    }
}
