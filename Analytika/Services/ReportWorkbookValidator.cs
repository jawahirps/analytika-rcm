using ClosedXML.Excel;

namespace Analytika.Services;

/// <summary>
/// Deterministic, in-process validation agent for generated tabular workbooks.
/// It requires no external model, API key, or network call.
/// </summary>
public sealed class ReportWorkbookValidator
{
    private const int HeaderRow = 8;

    public ReportWorkbookValidationResult Validate(
        string path,
        string expectedWorksheet,
        string expectedFacility,
        DateTime dateFrom,
        DateTime dateTo,
        int expectedDataRows,
        IReadOnlyList<string> expectedHeaders)
    {
        var errors = new List<string>();
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new(false, 0, ["Workbook file is missing or empty."]);

        using var workbook = new XLWorkbook(path);
        if (!workbook.TryGetWorksheet(expectedWorksheet, out var worksheet))
            return new(false, 0, [$"Expected worksheet '{expectedWorksheet}' was not found."]);

        if (worksheet.Pictures.Any())
            errors.Add("Embedded images are not permitted in tabular reports.");

        var headerText = string.Join(' ', worksheet.Range(1, 1, 6, expectedHeaders.Count)
            .CellsUsed()
            .Select(cell => cell.GetString()));
        if (headerText.Contains("GHAF", StringComparison.OrdinalIgnoreCase)
            || headerText.Contains("BUSINESS SERVICES", StringComparison.OrdinalIgnoreCase)
            || headerText.Contains("INTELLIGENCE", StringComparison.OrdinalIgnoreCase))
            errors.Add("Branding text is not permitted in tabular report headers.");

        if (!string.Equals(worksheet.Cell(1, 1).GetString().Trim(), "REPORT FILTERS", StringComparison.Ordinal))
            errors.Add("The report filter header is missing.");

        for (var index = 0; index < expectedHeaders.Count; index++)
        {
            var actual = worksheet.Cell(HeaderRow, index + 1).GetString().Trim();
            if (!string.Equals(actual, expectedHeaders[index], StringComparison.Ordinal))
                errors.Add($"Header {index + 1} expected '{expectedHeaders[index]}' but found '{actual}'.");
        }

        var facility = worksheet.Cell(2, 21).GetString().Trim();
        if (!string.Equals(facility, expectedFacility, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Facility metadata expected '{expectedFacility}' but found '{facility}'.");

        var expectedPeriod = $"{dateFrom:dd MMM yyyy} - {dateTo:dd MMM yyyy}";
        var period = worksheet.Cell(2, 3).GetString().Trim();
        if (!string.Equals(period, expectedPeriod, StringComparison.Ordinal))
            errors.Add($"Date range metadata expected '{expectedPeriod}' but found '{period}'.");

        var dataRows = CountContiguousDataRows(worksheet);
        if (dataRows != expectedDataRows)
            errors.Add($"Expected {expectedDataRows:N0} data rows but found {dataRows:N0}.");

        return new(errors.Count == 0, dataRows, errors);
    }

    private static int CountContiguousDataRows(IXLWorksheet worksheet)
    {
        var row = HeaderRow + 1;
        while (!worksheet.Cell(row, 2).IsEmpty())
            row++;
        return row - HeaderRow - 1;
    }
}

public sealed record ReportWorkbookValidationResult(bool IsValid, int DataRows, IReadOnlyList<string> Errors);
