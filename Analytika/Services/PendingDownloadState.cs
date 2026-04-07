using System.Threading.Channels;

namespace Analytika.Services;

/// <summary>
/// In-process singleton that tracks the state of the background pending-download service.
/// Also exposes a trigger channel so the web layer can wake the service immediately.
/// </summary>
public static class PendingDownloadState
{
    private static readonly object _lock = new();
    // Bounded channel (capacity 1) — deduplicates multiple rapid trigger calls
    private static readonly Channel<bool> _triggerChannel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private static PendingDownloadSnapshot _snap = new();

    /// <summary>Wake the background service for an immediate run.</summary>
    public static void Trigger() => _triggerChannel.Writer.TryWrite(true);

    internal static ChannelReader<bool> TriggerReader => _triggerChannel.Reader;

    public static void Start(int total)
    {
        lock (_lock)
        {
            _snap = new PendingDownloadSnapshot
            {
                IsRunning = true,
                StartedAt = DateTime.UtcNow,
                Total     = total
            };
        }
    }

    public static void Update(int done, int failed, string? currentFacility)
    {
        lock (_lock)
        {
            if (!_snap.IsRunning) return;
            _snap = _snap with { Done = done, Failed = failed, CurrentFacility = currentFacility ?? "" };
        }
    }

    public static void Finish(int done, int failed)
    {
        lock (_lock)
        {
            _snap = _snap with
            {
                IsRunning   = false,
                Done        = done,
                Failed      = failed,
                FinishedAt  = DateTime.UtcNow,
                LastRunAt   = DateTime.UtcNow,
                LastDone    = done
            };
        }
    }

    public static PendingDownloadSnapshot Get() { lock (_lock) return _snap; }
}

public record PendingDownloadSnapshot
{
    public bool      IsRunning       { get; init; }
    public int       Total           { get; init; }
    public int       Done            { get; init; }
    public int       Failed          { get; init; }
    public string    CurrentFacility { get; init; } = "";
    public DateTime  StartedAt       { get; init; }
    public DateTime? FinishedAt      { get; init; }
    public DateTime? LastRunAt       { get; init; }
    public int       LastDone        { get; init; }
}
