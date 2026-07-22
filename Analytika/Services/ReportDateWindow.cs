using System.Globalization;

namespace Analytika.Services;

/// <summary>
/// Authoritative server-side resolution of a report date window.
///
/// ROOT-CAUSE NOTE: the report form's JS writes dd/MM/yyyy into DateFrom/DateTo,
/// but MVC model binding parses DateTime with invariant culture (MM/dd/yyyy) —
/// so "31/12/2025" bound to null (silent last-month fallback → empty reports)
/// and "01/07/2026" bound to 7 January (wrong window). Presets are therefore
/// computed HERE from the DateRange label, and custom dates are parsed from the
/// raw form strings with explicit formats. Never trust the culture-bound values.
/// </summary>
public static class ReportDateWindow
{
    private static readonly string[] AcceptedFormats =
        { "dd/MM/yyyy", "yyyy-MM-dd", "d/M/yyyy", "dd-MM-yyyy" };

    public static bool TryParse(string? raw, out DateTime date)
        => DateTime.TryParseExact((raw ?? "").Trim(), AcceptedFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// Resolves (from, to) for a DateRange label. Presets ignore the raw inputs
    /// entirely. "Custom" requires BOTH raw dates to parse — returns false
    /// otherwise so the caller can reject instead of silently mis-windowing.
    /// </summary>
    public static bool TryResolve(string? dateRange, string? rawFrom, string? rawTo,
        DateTime today, out DateTime from, out DateTime to)
    {
        switch ((dateRange ?? "").Trim())
        {
            case "Today":
                from = to = today.Date; return true;
            case "ThisMonth":
                from = new DateTime(today.Year, today.Month, 1);
                to = from.AddMonths(1).AddDays(-1); return true;
            case "LastMonth":
                from = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                to = from.AddMonths(1).AddDays(-1); return true;
            case "ThisYear":
                from = new DateTime(today.Year, 1, 1);
                to = new DateTime(today.Year, 12, 31); return true;
            case "LastYear":
                from = new DateTime(today.Year - 1, 1, 1);
                to = new DateTime(today.Year - 1, 12, 31); return true;
            default: // Custom / unknown → require explicit, parseable dates
                if (TryParse(rawFrom, out from) && TryParse(rawTo, out to))
                {
                    if (to < from) (from, to) = (to, from);
                    return true;
                }
                from = to = default;
                return false;
        }
    }
}
