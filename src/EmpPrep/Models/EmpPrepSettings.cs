namespace EmpPrep.Models;

/// <summary>
/// Everything about a prep run that the user, not the library, decides. Persisted as JSON in the
/// extension key-value store and edited from the EMP Prep settings tab.
/// </summary>
/// <remarks>
/// Defaults are chosen so a fresh install produces a usable screenshot set and description with no
/// configuration at all. Two things genuinely cannot be defaulted and are validated at the point of
/// use rather than here: the announce URL (it contains the user's passkey) and image-host
/// credentials.
/// </remarks>
public sealed record EmpPrepSettings
{
    // ── Tracker ──────────────────────────────────────────────────────
    /// <summary>Full announce URL including passkey. Required before a torrent can be built.</summary>
    public string AnnounceUrl { get; init; } = "";

    /// <summary>Written to info.source. Distinguishes the infohash from other trackers' torrents.</summary>
    public string Source { get; init; } = "EMP";

    /// <summary>
    /// Piece length override in KiB. 0 (the default) picks the size automatically from the payload,
    /// which is the right choice for almost every upload; see TorrentBuilder.ChoosePieceLength.
    /// </summary>
    public int PieceLengthKiB { get; init; }

    /// <summary>Optional free-text comment embedded in the torrent.</summary>
    public string TorrentComment { get; init; } = "";

    // ── Output ───────────────────────────────────────────────────────
    /// <summary>Where prep artifacts are written. Blank uses a folder under the extension data directory.</summary>
    public string OutputDirectory { get; init; } = "";

    /// <summary>
    /// Where multi-file torrents are assembled and seeded from. Each pack gets a folder here holding
    /// hard links to its videos plus its Screens folder. A hard link is a second name for the same
    /// bytes, so this costs no disk space and never moves or renames anything in the library.
    /// </summary>
    /// <remarks>
    /// Deliberately has no default: it must be on the same drive as the media (hard links cannot
    /// cross drives) and outside every Cove library path (or Cove would import the links as
    /// duplicates). Pack builds refuse until it is set, and check both rules when they run.
    /// </remarks>
    public string PackDirectory { get; init; } = "";

    /// <summary>
    /// Build single-video torrents as a release-named folder hard-linked into
    /// <see cref="PackDirectory"/>, instead of hashing the file where it sits in the library.
    /// </summary>
    public bool UseHardlinkStaging { get; init; }

    // ── Screenshots ──────────────────────────────────────────────────
    /// <summary>
    /// Target spacing between frames. With the default 90s a 30-minute video yields 20 frames and
    /// an hour yields 40, which is the "one every one to two minutes" rule.
    /// </summary>
    public int SecondsPerFrame { get; init; } = 90;

    /// <summary>
    /// Floor on the frame count regardless of spacing, so short videos still get a full grid.
    /// Only a video too short to space this many frames apart gets fewer.
    /// </summary>
    public int MinFrameCount { get; init; } = 12;

    /// <summary>Upper bound, so a multi-hour file does not produce hundreds of screenshots.</summary>
    public int MaxFrameCount { get; init; } = 60;

    /// <summary>Minimum gap between frames. Defines "really short" — below this the count drops.</summary>
    public double MinFrameSpacingSeconds { get; init; } = 4;

    /// <summary>Fraction of the runtime skipped at the start, avoiding studio intros.</summary>
    public double SkipStartPercent { get; init; } = 2;

    /// <summary>Fraction of the runtime skipped at the end, avoiding credits and outros.</summary>
    public double SkipEndPercent { get; init; } = 2;

    /// <summary>"jpg" or "png". JPEG keeps the upload small; PNG avoids recompression artifacts.</summary>
    public string ScreenshotFormat { get; init; } = "jpg";

    /// <summary>JPEG quality 1-100, ignored for PNG.</summary>
    public int JpegQuality { get; init; } = 92;

    /// <summary>
    /// Scale frames to this width, preserving aspect. 0 keeps the source resolution, which is what
    /// a tracker wants — screenshots are meant to show the actual encode.
    /// </summary>
    public int ScreenshotWidth { get; init; }

    // ── Description ──────────────────────────────────────────────────
    /// <summary>Images per row in the generated BBCode screenshot grid.</summary>
    public int BbcodeColumns { get; init; } = 3;

    /// <summary>
    /// Title for the EMP upload form. Placeholders: {studio} {performers} {title} {date} {quality}
    /// {resolution} {height} {codec}. Performers are joined "A & B" / "A, B & C". Brackets and
    /// parentheses left empty by a missing value are dropped.
    /// </summary>
    public string PostTitleTemplate { get; init; } = "[{studio}] {performers} - {title} ({date}) [{quality}]";

    /// <summary>
    /// Release-name template. Placeholders: {studio} {performers} {title} {date} {resolution}
    /// {height} {codec} {source}.
    /// </summary>
    public string ReleaseNameTemplate { get; init; } = "{studio} - {performers} - {title} [{height}p]";

    /// <summary>
    /// Description template. Placeholders: {title} {studio} {performers} {date} {details}
    /// {tags} {mediainfo} {featured} {screenshots} {cover} {duration} {resolution} {filename} {filesize}.
    /// </summary>
    public string DescriptionTemplate { get; init; } = DefaultDescriptionTemplate;

    /// <remarks>
    /// Built on EMP's own section blocks, following the starter text its upload form begins with
    /// (<c>[quote][info][details][screens][/quote]</c>): each block is a section heading, followed by
    /// that section's content. The full-size images lead, outside the quote, and the screens follow
    /// it: EMP's screens block does not work inside a quote.
    /// </remarks>
    public const string DefaultDescriptionTemplate = """
        {featured}

        [quote]
        [info]
        [b]{title}[/b]
        Studio: {studio}
        Performers: {performers}
        Released: {date}

        {details}

        [details]
        {mediainfo}
        [/quote]

        [screens]
        {screenshots}
        """;

    // ── Packs ────────────────────────────────────────────────────────
    /// <summary>
    /// Title for a pack's EMP post. Placeholders: {studio} (only when every scene shares one),
    /// {performers} (up to three, otherwise "Various"), {packtype} ("Megapack" / "Minipack"),
    /// {count}, {quality} (e.g. "1080p" or "720p-4K").
    /// </summary>
    public string PackTitleTemplate { get; init; } = "[{studio}] {performers} - {packtype} ({count} Scenes) [{quality}]";

    /// <summary>
    /// A content tag goes into a pack's Tags field when at least this share of its scenes carry it.
    /// The description's tag list shows every tag with its count regardless.
    /// </summary>
    public int PackTagThresholdPercent { get; init; } = 25;

    /// <summary>Add "megapack" or "minipack" to a pack's tags.</summary>
    public bool IncludePackTypeTag { get; init; } = true;

    /// <summary>
    /// Description for a pack's EMP post. Placeholders: {cover} {theme} {packtitle} {count}
    /// {packtype} {runtime} {size} {stats} {performers} {sources} {tags} {screens} {screensnote}.
    /// </summary>
    public string PackDescriptionTemplate { get; init; } = DefaultPackDescriptionTemplate;

    /// <remarks>
    /// EMP's pack rules ask for a summary of what the pack is and what it has in common rather than
    /// a write-up of every scene, plus screens in the description. So this leads with the user's
    /// theme text, then the pack's numbers, who is in it, where it is from and what it contains,
    /// and finally the contact sheets, after the quote since EMP's screens block does not work inside one.
    /// </remarks>
    public const string DefaultPackDescriptionTemplate = """
        {cover}

        [quote]
        [info]
        [b]{packtitle}[/b]

        {theme}

        [details]
        {stats}

        [b]Performers[/b]
        {performers}

        [b]Sources[/b]
        {sources}

        [b]Tags[/b]
        {tags}
        [/quote]

        [screens]
        {screensnote}
        {screens}
        """;

    /// <summary>Pack description templates shipped as the default before the current one.</summary>
    private static readonly string[] PreviousPackDescriptionTemplates =
    [
        // Screens inside the quote.
        """
        {cover}

        [quote]
        [info]
        [b]{packtitle}[/b]

        {theme}

        [details]
        {stats}

        [b]Performers[/b]
        {performers}

        [b]Sources[/b]
        {sources}

        [b]Tags[/b]
        {tags}

        [screens]
        {screensnote}
        {screens}
        [/quote]
        """,
    ];

    // ── Tags ─────────────────────────────────────────────────────────
    /// <summary>Lowercase tags. EMP tags are conventionally lowercase.</summary>
    public bool LowercaseTags { get; init; } = true;

    /// <summary>Include performer names as tags (dotted), which EMP expects.</summary>
    public bool IncludePerformerTags { get; init; } = true;

    /// <summary>Include the studio name as a tag.</summary>
    public bool IncludeStudioTags { get; init; }

    /// <summary>
    /// Tag the studio's website domain (e.g. "pornhub.com"), which is how EMP tags a studio or site.
    /// Taken from the studio's URLs in Cove; skipped when it has none.
    /// </summary>
    public bool IncludeStudioWebsiteTag { get; init; } = true;

    /// <summary>Tag the quality, e.g. "1080p". EMP's upload guide asks for it.</summary>
    public bool IncludeQualityTag { get; init; } = true;

    /// <summary>Tag the release date as "2024", "2024.05" and "2024.05.01".</summary>
    public bool IncludeDateTags { get; init; }

    /// <summary>Tag the video codec, e.g. "h264".</summary>
    public bool IncludeCodecTag { get; init; }

    /// <summary>Tags never emitted, comma separated. Useful for internal organisational tags.</summary>
    public string ExcludedTags { get; init; } = "";

    // ── Image host (hamsterimg.net) ──────────────────────────────────
    /// <summary>
    /// hamsterimg.net API key, from the account's /settings/api page. Uploading is unavailable
    /// until it is set; everything else works without it.
    /// </summary>
    public string HamsterImgApiKey { get; init; } = "";

    /// <summary>Optional hamsterimg album id to file uploads under. Must be an album the key owns.</summary>
    public string HamsterImgAlbumId { get; init; } = "";

    // ── Uploaded images ──────────────────────────────────────────────
    /// <summary>
    /// How many full-size images each upload carries. The library cover counts as one when it is
    /// used; frames spread through the video make up the rest.
    /// </summary>
    public int FullSizeImageCount { get; init; } = 2;

    /// <summary>Use the video's cover art from the Cove library as one of the full-size images.</summary>
    public bool IncludeCoverImage { get; init; } = true;

    /// <summary>Frames per row in the contact sheet, which is the screens section of every upload.</summary>
    public int ContactSheetColumns { get; init; } = 4;

    /// <summary>Width of each frame inside the contact sheet, in pixels.</summary>
    public int ContactSheetTileWidth { get; init; } = 480;

    /// <summary>Print each frame's timestamp in the corner of its tile on the contact sheet.</summary>
    public bool ContactSheetTimestamps { get; init; } = true;

    /// <summary>Put the file name and resolution in a band across the top of the contact sheet.</summary>
    public bool ContactSheetHeader { get; init; } = true;

    /// <summary>
    /// Description templates this extension shipped as its default before the current one.
    /// </summary>
    /// <remarks>
    /// Settings are stored as one object, so the first save writes whatever template was the default
    /// at the time, and a later, better default would never reach that user. A stored template that
    /// is exactly an earlier default was never customised, so it is upgraded; anything the user
    /// edited — even by one character — is kept as-is. Add the outgoing default here whenever
    /// <see cref="DefaultDescriptionTemplate"/> changes.
    /// </remarks>
    private static readonly string[] PreviousDescriptionTemplates =
    [
        // Original release.
        """
        [size=4][b]{title}[/b][/size]

        [b]Studio:[/b] {studio}
        [b]Performers:[/b] {performers}
        [b]Date:[/b] {date}
        [b]Runtime:[/b] {duration}

        {details}

        [b]Media info[/b]
        [quote]{mediainfo}[/quote]

        [b]Screens[/b]
        {screenshots}
        """,
        // Added the full-size images.
        """
        [size=4][b]{title}[/b][/size]

        {featured}

        [b]Studio:[/b] {studio}
        [b]Performers:[/b] {performers}
        [b]Date:[/b] {date}
        [b]Runtime:[/b] {duration}

        {details}

        [b]Media info[/b]
        [quote]{mediainfo}[/quote]

        [b]Screens[/b]
        {screenshots}
        """,
        // EMP's section blocks, with the screens inside the quote.
        """
        {featured}

        [quote]
        [info]
        [b]{title}[/b]
        Studio: {studio}
        Performers: {performers}
        Released: {date}

        {details}

        [details]
        {mediainfo}

        [screens]
        {screenshots}
        [/quote]
        """,
    ];

    internal static bool IsPreviousDefault(string template) => IsOneOf(template, PreviousDescriptionTemplates);

    internal static bool IsPreviousPackDefault(string template) => IsOneOf(template, PreviousPackDescriptionTemplates);

    private static bool IsOneOf(string template, IEnumerable<string> templates)
    {
        var normalized = NormalizeTemplate(template);
        return templates.Any(previous => NormalizeTemplate(previous) == normalized);
    }

    /// <summary>Compare templates ignoring line-ending style and surrounding whitespace only.</summary>
    private static string NormalizeTemplate(string template)
        => string.Join('\n', template.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim();

    /// <summary>Normalise anything out of range that a hand-edited store value could contain.</summary>
    public EmpPrepSettings Normalized() => this with
    {
        AnnounceUrl = AnnounceUrl.Trim(),
        Source = Source.Trim(),
        PieceLengthKiB = PieceLengthKiB <= 0 ? 0 : Services.TorrentBuilder.NormalizePieceLength(PieceLengthKiB * 1024L) / 1024,
        OutputDirectory = OutputDirectory.Trim(),
        PackDirectory = PackDirectory.Trim(),
        PackTagThresholdPercent = Math.Clamp(PackTagThresholdPercent, 0, 100),
        PackTitleTemplate = string.IsNullOrWhiteSpace(PackTitleTemplate)
            ? new EmpPrepSettings().PackTitleTemplate
            : PackTitleTemplate,
        PackDescriptionTemplate = string.IsNullOrWhiteSpace(PackDescriptionTemplate) || IsPreviousPackDefault(PackDescriptionTemplate)
            ? DefaultPackDescriptionTemplate
            : PackDescriptionTemplate,
        SecondsPerFrame = Math.Clamp(SecondsPerFrame, 5, 3600),
        MinFrameCount = Math.Clamp(MinFrameCount, 1, 200),
        MaxFrameCount = Math.Clamp(MaxFrameCount, 1, 200),
        MinFrameSpacingSeconds = Math.Clamp(MinFrameSpacingSeconds, 0.5, 600),
        SkipStartPercent = Math.Clamp(SkipStartPercent, 0, 40),
        SkipEndPercent = Math.Clamp(SkipEndPercent, 0, 40),
        ScreenshotFormat = ScreenshotFormat.Trim().ToLowerInvariant() is "png" ? "png" : "jpg",
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        ScreenshotWidth = ScreenshotWidth < 0 ? 0 : ScreenshotWidth,
        BbcodeColumns = Math.Clamp(BbcodeColumns, 1, 6),
        PostTitleTemplate = string.IsNullOrWhiteSpace(PostTitleTemplate)
            ? new EmpPrepSettings().PostTitleTemplate
            : PostTitleTemplate,
        DescriptionTemplate = string.IsNullOrWhiteSpace(DescriptionTemplate) || IsPreviousDefault(DescriptionTemplate)
            ? DefaultDescriptionTemplate
            : DescriptionTemplate,
        HamsterImgApiKey = HamsterImgApiKey.Trim(),
        HamsterImgAlbumId = HamsterImgAlbumId.Trim(),
        ContactSheetColumns = Math.Clamp(ContactSheetColumns, 1, 8),
        ContactSheetTileWidth = Math.Clamp(ContactSheetTileWidth, 160, 1920),
        FullSizeImageCount = Math.Clamp(FullSizeImageCount, 1, 6),
    };
}
