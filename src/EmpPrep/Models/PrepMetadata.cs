namespace EmpPrep.Models;

/// <summary>Technical detail about the file being uploaded, for the description's media-info block.</summary>
public sealed record PrepMediaInfo
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public string Container { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double DurationSeconds { get; init; }
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public double FrameRate { get; init; }
    public long BitRateBps { get; init; }
    public int? AudioChannels { get; init; }
    public int? AudioSampleRate { get; init; }
    public long? AudioBitRateBps { get; init; }

    /// <summary>Resolution label, e.g. "1920x1080".</summary>
    public string Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "unknown";

    /// <summary>Quality label trackers tag with, e.g. "1080p". Empty when the size is unknown.</summary>
    public string QualityLabel => Services.MetadataBuilder.QualityLabel(Width, Height);
}

/// <summary>
/// Everything derived from the library for one video, before the user edits anything.
/// </summary>
public sealed record PrepMetadata
{
    public int VideoId { get; init; }
    public string Title { get; init; } = "";
    public string? Studio { get; init; }
    public IReadOnlyList<string> Performers { get; init; } = [];
    public IReadOnlyList<string> LibraryTags { get; init; } = [];
    /// <summary>Dotted, de-duplicated tag tokens ready for the upload form.</summary>
    public IReadOnlyList<string> FormattedTags { get; init; } = [];
    public string? Details { get; init; }
    public DateOnly? Date { get; init; }
    public string? Code { get; init; }
    public string? Director { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = [];
    public PrepMediaInfo MediaInfo { get; init; } = new();
    public string ReleaseName { get; init; } = "";
    public bool HasSprite { get; init; }
    /// <summary>Blob id of the video's cover art in the Cove library, if it has one.</summary>
    public string? CoverBlobId { get; init; }
    /// <summary>The studio's website domain (e.g. "pornhub.com"), from its URLs in the library.</summary>
    public string? StudioWebsite { get; init; }
    /// <summary>Title for the EMP upload form, rendered from the post-title template.</summary>
    public string PostTitle { get; init; } = "";
}

/// <summary>The assembled, copy-ready upload package for one video.</summary>
public sealed record PrepPackage
{
    public Guid ItemId { get; init; }
    public int VideoId { get; init; }
    public string Status { get; init; } = "";
    public string ReleaseName { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>The Title field of the EMP upload form.</summary>
    public string PostTitle { get; init; } = "";
    /// <summary>
    /// Fields holding a stored user edit rather than the generated value: any of "title",
    /// "postTitle", "tags", "description". Lets the page resubmit only real edits.
    /// </summary>
    public IReadOnlyList<string> EditedFields { get; init; } = [];
    public string TagString { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Studio { get; init; }
    public IReadOnlyList<string> Performers { get; init; } = [];
    public string Description { get; init; } = "";
    public string MediaInfoText { get; init; } = "";
    public IReadOnlyList<PrepScreenshot> Screenshots { get; init; } = [];
    /// <summary>
    /// Full-size link of the first featured image, for the upload form's cover-image field. Null
    /// until that image has been uploaded.
    /// </summary>
    public string? CoverImageUrl { get; init; }
    /// <summary>Whether the video has cover art in the Cove library.</summary>
    public bool HasLibraryCover { get; init; }
    public string? TorrentPath { get; init; }
    public string? InfoHash { get; init; }
    public long? TorrentTotalBytes { get; init; }
    public int? TorrentPieceLength { get; init; }
    public int? TorrentPieceCount { get; init; }
    public string? MediaPath { get; init; }
    public string? LastError { get; init; }
    public PrepMetadata? Derived { get; init; }
}

/// <summary>What an image in the upload set is.</summary>
public static class PrepImageKind
{
    /// <summary>A frame extracted from the video.</summary>
    public const string Frame = "frame";
    /// <summary>The video's cover art from the Cove library.</summary>
    public const string Cover = "cover";
    /// <summary>A grid of every extracted frame in one image.</summary>
    public const string ContactSheet = "sheet";
}

/// <summary>
/// One image in the upload set: an extracted frame, the library cover, or the contact sheet.
/// </summary>
/// <remarks>
/// <see cref="Featured"/> images are uploaded and shown full size at the top of the description, and
/// the first supplies the upload form's cover image. The contact sheet is uploaded as the screens
/// section. Every other frame stays local and only feeds the contact sheet.
/// </remarks>
public sealed record PrepScreenshot(double Seconds, string Path)
{
    public string Kind { get; init; } = PrepImageKind.Frame;
    public bool Featured { get; init; }

    /// <summary>Direct link to the full-size image on the host.</summary>
    public string? Url { get; init; }
    /// <summary>Direct link to the host's thumbnail of it.</summary>
    public string? ThumbUrl { get; init; }
    /// <summary>The host's viewer page for it.</summary>
    public string? ViewerUrl { get; init; }

    /// <summary>File name only, for display and for the not-yet-uploaded placeholder.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Shown full size in the description. The contact sheet is excluded even if an older record
    /// flagged it, since it always forms the screens section.
    /// </summary>
    public bool IsFullSize => Featured && Kind != PrepImageKind.ContactSheet;

    /// <summary>
    /// Whether this image is uploaded to the host: the full-size images and the contact sheet. Other
    /// frames stay local; they only exist to build the sheet.
    /// </summary>
    public bool Uploads => IsFullSize || Kind == PrepImageKind.ContactSheet;
}
