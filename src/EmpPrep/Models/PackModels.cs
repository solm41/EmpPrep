namespace EmpPrep.Models;

/// <summary>One scene in a pack, with what its prep has produced so far.</summary>
public sealed record PackScene
{
    public required Guid ItemId { get; init; }
    public required int VideoId { get; init; }
    public required PrepMetadata Metadata { get; init; }

    /// <summary>The video file in the Cove library. Linked into the pack, never moved.</summary>
    public string SourcePath => Metadata.MediaInfo.FilePath;

    /// <summary>The scene's contact sheet, if screens have been generated.</summary>
    public PrepScreenshot? Sheet { get; init; }

    /// <summary>The scene's library cover image, if it has one.</summary>
    public PrepScreenshot? Cover { get; init; }
}

/// <summary>A value and how many scenes it applies to.</summary>
public sealed record CountedValue(string Name, int Count);

/// <summary>Numbers describing a whole pack, for the description's stats block.</summary>
public sealed record PackStats
{
    public int SceneCount { get; init; }
    public double TotalDurationSeconds { get; init; }
    public long TotalSizeBytes { get; init; }
    public double ShortestSeconds { get; init; }
    public double LongestSeconds { get; init; }
    public double AverageSeconds { get; init; }
    public double MedianSeconds { get; init; }
    public long AverageSizeBytes { get; init; }

    /// <summary>Quality label ("4K", "1080p", …) → scene count, best quality first.</summary>
    public IReadOnlyList<CountedValue> Qualities { get; init; } = [];
    /// <summary>Video codec → scene count, most common first.</summary>
    public IReadOnlyList<CountedValue> Codecs { get; init; } = [];
    /// <summary>Rounded frame rate ("30 fps") → scene count, most common first.</summary>
    public IReadOnlyList<CountedValue> FrameRates { get; init; } = [];

    public DateOnly? EarliestDate { get; init; }
    public DateOnly? LatestDate { get; init; }

    /// <summary>Performer → number of scenes they appear in, most frequent first.</summary>
    public IReadOnlyList<CountedValue> Performers { get; init; } = [];
    /// <summary>Studio name → scene count, most frequent first.</summary>
    public IReadOnlyList<CountedValue> Studios { get; init; } = [];
    /// <summary>Studio website domain → scene count, most frequent first.</summary>
    public IReadOnlyList<CountedValue> Websites { get; init; } = [];
    /// <summary>Content tag (dotted) → number of scenes carrying it, most common first.</summary>
    public IReadOnlyList<CountedValue> Tags { get; init; } = [];
}

/// <summary>A problem with a pack against EMP's pack rules. Errors block the torrent; warnings do not.</summary>
public sealed record PackIssue(string Severity, string Message)
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>The assembled, copy-ready EMP post and torrent for a multi-scene pack.</summary>
public sealed record PackPackage
{
    public Guid BatchId { get; init; }
    /// <summary>"Megapack" at or above EMP's 10-scene threshold, otherwise "Minipack".</summary>
    public string PackType { get; init; } = "";
    public string Theme { get; init; } = "";
    public string PostTitle { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string TagString { get; init; } = "";
    public string Description { get; init; } = "";
    public Guid? CoverItemId { get; init; }
    public string? CoverImageUrl { get; init; }
    /// <summary>Fields holding a stored edit rather than the generated value.</summary>
    public IReadOnlyList<string> EditedFields { get; init; } = [];
    public PackStats Stats { get; init; } = new();
    public IReadOnlyList<PackIssue> Issues { get; init; } = [];
    /// <summary>How many scenes' screens the description shows (all, or a representative share).</summary>
    public int ScreensShown { get; init; }

    /// <summary>The folder the pack is linked into, once built — what the torrent client seeds from.</summary>
    public string? PackFolder { get; init; }
    public string? TorrentPath { get; init; }
    public string? InfoHash { get; init; }
    public long? TorrentTotalBytes { get; init; }
    public int? TorrentPieceLength { get; init; }
    public int? TorrentPieceCount { get; init; }
}

/// <summary>User edits to a pack's post. Null or blank means "use the generated value".</summary>
public sealed record PackEdits
{
    public string? Theme { get; init; }
    public string? PostTitle { get; init; }
    public string? Tags { get; init; }
    public string? Description { get; init; }
    public Guid? CoverItemId { get; init; }
}
