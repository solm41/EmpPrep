using System.Collections.Concurrent;

namespace EmpPrep.Services;

public sealed record PrepJobState(
    string JobId,
    Guid ItemId,
    string Kind,
    double Percent,
    string? Message,
    bool Finished,
    string? Error);

/// <summary>
/// Tracks the progress of the long-running prep steps so the page can show a real progress bar.
/// </summary>
/// <remarks>
/// Hashing a multi-gigabyte file and extracting forty frames both take long enough that a spinner
/// is not good enough feedback. Cove's job list shows the work, but the prep page needs it per item,
/// which is what this keyed view provides.
///
/// State is deliberately in-memory and per-item: it describes work happening right now, so losing
/// it on restart is correct — the durable outcome is the item's status and artifacts in the
/// database. Only the latest run per item is kept, and finished entries are pruned so a long
/// session cannot accumulate them without bound.
/// </remarks>
public sealed class PrepJobTracker
{
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private sealed record Entry(PrepJobState State, DateTime? FinishedAtUtc);

    public void Start(Guid itemId, string jobId, string kind)
        => _entries[itemId] = new Entry(new PrepJobState(jobId, itemId, kind, 0, null, false, null), null);

    public void Report(Guid itemId, double fraction, string? message = null)
    {
        _entries.AddOrUpdate(
            itemId,
            _ => new Entry(new PrepJobState("", itemId, "", Clamp(fraction), message, false, null), null),
            (_, existing) => existing.FinishedAtUtc is not null
                ? existing
                : existing with { State = existing.State with { Percent = Clamp(fraction), Message = message } });
    }

    public void Complete(Guid itemId, string? error = null)
    {
        Prune();
        _entries.AddOrUpdate(
            itemId,
            _ => new Entry(new PrepJobState("", itemId, "", 1, null, true, error), DateTime.UtcNow),
            (_, existing) => new Entry(
                existing.State with { Percent = error is null ? 1 : existing.State.Percent, Finished = true, Error = error },
                DateTime.UtcNow));
    }

    public IReadOnlyList<PrepJobState> Active()
    {
        Prune();
        return _entries.Values.Select(entry => entry.State).ToList();
    }

    public PrepJobState? For(Guid itemId)
        => _entries.TryGetValue(itemId, out var entry) ? entry.State : null;

    /// <summary>Report progress into the tracker as an <see cref="IProgress{T}"/>.</summary>
    public IProgress<double> ProgressFor(Guid itemId, string? message = null)
        => new Progress<double>(fraction => Report(itemId, fraction, message));

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - FinishedRetention;
        foreach (var (itemId, entry) in _entries)
        {
            if (entry.FinishedAtUtc is { } finishedAt && finishedAt < cutoff)
                _entries.TryRemove(itemId, out _);
        }
    }

    private static double Clamp(double fraction) => Math.Clamp(fraction, 0, 1);
}
