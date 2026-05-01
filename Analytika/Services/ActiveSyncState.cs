namespace Analytika.Services;

/// <summary>
/// In-process singleton that tracks the currently-running month-wise sync.
/// Updated by SyncAllFacilitiesStream, read by StatusBar endpoint.
/// </summary>
public static class ActiveSyncState
{
    private static readonly object _lock = new();

    // Snapshot — replaced atomically under lock
    private static SyncSnapshot _snap = new();

    public static void Start(int totalFacilities, int totalMonths)
    {
        lock (_lock)
        {
            _snap = new SyncSnapshot
            {
                IsRunning = true,
                StartedAt = DateTime.UtcNow,
                TotalFacilities = totalFacilities,
                TotalMonths = totalMonths,
                TotalSteps = totalFacilities * totalMonths
            };
        }
    }

    public static void Update(int stepsDone, int facilityIndex, string facilityName,
                              string month, int recordsSaved, int filesDownloaded, int pct)
    {
        lock (_lock)
        {
            if (!_snap.IsRunning) return;
            _snap = _snap with
            {
                StepsDone = stepsDone,
                FacilityIndex = facilityIndex,
                CurrentFacility = facilityName,
                CurrentMonth = month,
                RecordsSaved = recordsSaved,
                FilesDownloaded = filesDownloaded,
                Pct = pct
            };
        }
    }

    public static void Finish(int recordsSaved, int filesDownloaded)
    {
        lock (_lock)
        {
            _snap = _snap with
            {
                IsRunning = false,
                Pct = 100,
                RecordsSaved = recordsSaved,
                FilesDownloaded = filesDownloaded,
                FinishedAt = DateTime.UtcNow
            };
        }
    }

    public static SyncSnapshot Get()
    {
        lock (_lock) return _snap;
    }
}

public record SyncSnapshot
{
    public bool IsRunning { get; init; }
    public int Pct { get; init; }
    public int FacilityIndex { get; init; }
    public int TotalFacilities { get; init; }
    public int TotalMonths { get; init; }
    public int TotalSteps { get; init; }
    public int StepsDone { get; init; }
    public string CurrentFacility { get; init; } = "";
    public string CurrentMonth { get; init; } = "";
    public int RecordsSaved { get; init; }
    public int FilesDownloaded { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}
