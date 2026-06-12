namespace Analytika.Services;

/// <summary>
/// Tracks the active report generation job so the UI can show live progress.
/// One report job is expected at a time in the local in-process workflow.
/// </summary>
public static class ReportGenerationState
{
    private static readonly object _lock = new();
    private static ReportGenerationSnapshot _snap = new();

    public static void Start(int reportRequestId, string reportId, string reportType, string facility, string dateRange)
    {
        lock (_lock)
        {
            _snap = new ReportGenerationSnapshot
            {
                IsRunning = true,
                ReportRequestId = reportRequestId,
                ReportId = reportId,
                ReportType = reportType,
                Facility = facility,
                DateRange = dateRange,
                Stage = "Queued",
                Message = "Waiting to start report generation...",
                Pct = 0,
                StartedAt = DateTime.UtcNow
            };
        }
    }

    public static void Update(int reportRequestId, string stage, int pct, int done = 0, int total = 0, string? message = null)
    {
        lock (_lock)
        {
            if (!_snap.IsRunning || _snap.ReportRequestId != reportRequestId)
                return;

            _snap = _snap with
            {
                Stage = stage,
                Pct = Math.Clamp(pct, 0, 100),
                Done = done,
                Total = total > 0 ? total : _snap.Total,
                Message = message ?? stage
            };
        }
    }

    public static void Finish(int reportRequestId, string? message = null)
    {
        lock (_lock)
        {
            if (_snap.ReportRequestId != reportRequestId)
                return;

            _snap = _snap with
            {
                IsRunning = false,
                Pct = 100,
                Stage = "Completed",
                Message = message ?? "Report generation complete.",
                FinishedAt = DateTime.UtcNow
            };
        }
    }

    public static void Fail(int reportRequestId, string message)
    {
        lock (_lock)
        {
            if (_snap.ReportRequestId != reportRequestId)
                return;

            _snap = _snap with
            {
                IsRunning = false,
                Stage = "Failed",
                Message = message,
                FinishedAt = DateTime.UtcNow
            };
        }
    }

    public static ReportGenerationSnapshot Get()
    {
        lock (_lock) return _snap;
    }
}

public record ReportGenerationSnapshot
{
    public bool IsRunning { get; init; }
    public int ReportRequestId { get; init; }
    public string ReportId { get; init; } = "";
    public string ReportType { get; init; } = "";
    public string Facility { get; init; } = "";
    public string DateRange { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public int Pct { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
