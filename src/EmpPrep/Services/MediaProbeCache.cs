using System.Collections.Concurrent;

namespace EmpPrep.Services;

/// <summary>Audio detail Cove's scan does not store, read with ffprobe.</summary>
public sealed record AudioDetails(int? Channels, int? SampleRate, long? BitRate);

/// <summary>
/// Remembers ffprobe results per file, so opening a batch never starts a process per video.
/// </summary>
/// <remarks>
/// Probing is the expensive part of reading a video's details: it starts a process and opens the
/// file — over a network share for a large library. Doing that for every video on every page load
/// made a 130-video batch take over ten seconds to open. Results are keyed by path, size and
/// modification time as recorded in Cove's database, so a changed file is probed again while
/// checking the key never touches the disk.
/// </remarks>
public sealed class MediaProbeCache
{
    private readonly ConcurrentDictionary<string, AudioDetails> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string path, long size, DateTime modified) => $"{path}|{size}|{modified.Ticks}";

    public bool TryGet(string key, out AudioDetails details) => _entries.TryGetValue(key, out details!);

    public void Set(string key, AudioDetails details) => _entries[key] = details;
}
