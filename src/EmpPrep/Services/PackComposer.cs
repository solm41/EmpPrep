using System.Globalization;
using System.Text;

using EmpPrep.Models;

namespace EmpPrep.Services;

/// <summary>
/// Turns a set of scenes into a pack's EMP post: its numbers, title, tags, description and the
/// checks against EMP's pack rules. Pure — every input is passed in, so it is testable in isolation.
/// </summary>
public static class PackComposer
{
    /// <summary>EMP's definition: a megapack has at least this many scenes.</summary>
    public const int MegapackMinimumScenes = 10;

    /// <summary>Up to this many scenes, the description shows every scene's screens.</summary>
    public const int ShowAllScreensUpTo = 50;

    /// <summary>Above that, EMP allows a representative 10–20% of the screens; this takes the middle.</summary>
    public const double RepresentativeShare = 0.15;

    /// <summary>EMP's cap on screens posted in a pack description.</summary>
    public const int MaximumScreensShown = 100;

    public static string PackType(int sceneCount) => sceneCount >= MegapackMinimumScenes ? "Megapack" : "Minipack";

    // ── Stats ────────────────────────────────────────────────────────

    public static PackStats Stats(IReadOnlyList<PackScene> scenes)
    {
        var durations = scenes.Select(scene => scene.Metadata.MediaInfo.DurationSeconds).Where(seconds => seconds > 0).Order().ToList();
        var sizes = scenes.Select(scene => scene.Metadata.MediaInfo.FileSizeBytes).Where(bytes => bytes > 0).ToList();
        var dates = scenes.Select(scene => scene.Metadata.Date).OfType<DateOnly>().Order().ToList();

        return new PackStats
        {
            SceneCount = scenes.Count,
            TotalDurationSeconds = durations.Sum(),
            TotalSizeBytes = sizes.Sum(),
            ShortestSeconds = durations.Count > 0 ? durations[0] : 0,
            LongestSeconds = durations.Count > 0 ? durations[^1] : 0,
            AverageSeconds = durations.Count > 0 ? durations.Average() : 0,
            MedianSeconds = Median(durations),
            AverageSizeBytes = sizes.Count > 0 ? (long)sizes.Average() : 0,
            Qualities = Count(scenes.Select(scene => QualityName(scene.Metadata.MediaInfo.QualityLabel)))
                .OrderByDescending(entry => QualityRank(entry.Name))
                .ToList(),
            Codecs = Ranked(Count(scenes.Select(scene => scene.Metadata.MediaInfo.VideoCodec.Trim().ToLowerInvariant()))),
            FrameRates = Ranked(Count(scenes.Select(scene => FrameRateName(scene.Metadata.MediaInfo.FrameRate)))),
            EarliestDate = dates.Count > 0 ? dates[0] : null,
            LatestDate = dates.Count > 0 ? dates[^1] : null,
            Performers = Ranked(Count(scenes.SelectMany(scene => scene.Metadata.Performers.Distinct(StringComparer.OrdinalIgnoreCase)))),
            Studios = Ranked(Count(scenes.Select(scene => scene.Metadata.Studio))),
            Websites = Ranked(Count(scenes.Select(scene => scene.Metadata.StudioWebsite))),
            // Content tags only: performer, website and quality tags are covered by their own lists.
            Tags = Ranked(Count(scenes.SelectMany(scene =>
                EmpTagFormatter.FormatTags(scene.Metadata.LibraryTags).Distinct(StringComparer.OrdinalIgnoreCase)))),
        };
    }

    private static double Median(IReadOnlyList<double> sorted) => sorted.Count switch
    {
        0 => 0,
        _ when sorted.Count % 2 == 1 => sorted[sorted.Count / 2],
        _ => (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2,
    };

    /// <summary>Count non-blank values case-insensitively, keeping the first spelling seen.</summary>
    private static List<CountedValue> Count(IEnumerable<string?> values)
    {
        var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            counts[value] = counts.TryGetValue(value, out var existing) ? (existing.Display, existing.Count + 1) : (value, 1);
        }

        return counts.Values.Select(entry => new CountedValue(entry.Display, entry.Count)).ToList();
    }

    /// <summary>Most frequent first; ties alphabetical, so the order is stable.</summary>
    private static List<CountedValue> Ranked(IEnumerable<CountedValue> values)
        => values.OrderByDescending(value => value.Count).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Show 2160p as "4K", the way people read it; other labels as they are.</summary>
    public static string QualityName(string label) => label == "2160p" ? "4K" : label;

    private static int QualityRank(string name) => name switch
    {
        "8K" => 900, "5K" => 800, "4K" => 700, "1440p" => 600, "1080p" => 500, "720p" => 400,
        "540p" => 300, "480p" => 200, "360p" => 150, "240p" => 100, "144p" => 50, _ => 0,
    };

    /// <summary>Round to a whole frame rate, so 29.97 and 30 count together as "30 fps".</summary>
    private static string FrameRateName(double frameRate)
        => frameRate > 0 ? $"{Math.Round(frameRate).ToString(CultureInfo.InvariantCulture)} fps" : "";

    // ── Title and tags ───────────────────────────────────────────────

    public static string Title(PackStats stats, EmpPrepSettings settings)
    {
        var performers = stats.Performers.Count is > 0 and <= 3
            ? MetadataBuilder.JoinPerformers(stats.Performers.Select(performer => performer.Name).ToList())
            : stats.Performers.Count > 3 ? "Various" : "";

        var qualities = stats.Qualities.Select(quality => quality.Name).ToList();
        var quality = qualities.Count switch
        {
            0 => "",
            1 => qualities[0],
            // Best first in the list, so the range reads low to high.
            _ => $"{qualities[^1]}-{qualities[0]}",
        };

        return MetadataBuilder.RenderName(settings.PackTitleTemplate, new Dictionary<string, string>
        {
            // Only name the studio when every scene shares it; a mixed pack would be misleading.
            ["studio"] = stats.Studios.Count == 1 && stats.Studios[0].Count == stats.SceneCount ? stats.Studios[0].Name : "",
            ["performers"] = performers,
            ["packtype"] = PackType(stats.SceneCount),
            ["count"] = stats.SceneCount.ToString(CultureInfo.InvariantCulture),
            ["quality"] = quality,
        });
    }

    /// <summary>
    /// The pack's Tags field: every performer and source site, each quality present, the content
    /// tags common enough to describe the pack, and the pack type.
    /// </summary>
    /// <remarks>
    /// Content tags are filtered by <see cref="EmpPrepSettings.PackTagThresholdPercent"/>: a tag on
    /// one scene of forty would make the pack show up in searches it does not really answer. The
    /// description still lists every tag with its count.
    /// </remarks>
    public static IReadOnlyList<string> Tags(PackStats stats, EmpPrepSettings settings)
    {
        var sources = new List<string>();
        if (settings.IncludePerformerTags)
            sources.AddRange(stats.Performers.Select(performer => performer.Name));
        if (settings.IncludeStudioWebsiteTag)
            sources.AddRange(stats.Websites.Select(site => site.Name));
        if (settings.IncludeStudioTags)
            sources.AddRange(stats.Studios.Select(studio => studio.Name));
        if (settings.IncludeQualityTag)
            sources.AddRange(stats.Qualities.Select(quality => quality.Name == "4K" ? "2160p" : quality.Name));

        var minimum = Math.Max(1, (int)Math.Ceiling(stats.SceneCount * settings.PackTagThresholdPercent / 100d));
        sources.AddRange(stats.Tags.Where(tag => tag.Count >= minimum).Select(tag => tag.Name));

        if (settings.IncludePackTypeTag && stats.SceneCount > 1)
            sources.Add(PackType(stats.SceneCount));

        var excluded = settings.ExcludedTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return EmpTagFormatter.FormatTags(sources, settings.LowercaseTags, excluded);
    }

    // ── Screens ──────────────────────────────────────────────────────

    /// <summary>
    /// Which scenes' screens the description shows: all of them up to <see cref="ShowAllScreensUpTo"/>
    /// scenes, otherwise a representative share spread across the pack, as EMP's rules allow for
    /// large packs whose torrent carries every screen.
    /// </summary>
    public static IReadOnlyList<int> ScreensToShow(int sceneCount)
    {
        if (sceneCount <= ShowAllScreensUpTo)
            return Enumerable.Range(0, sceneCount).ToList();

        var count = Math.Clamp((int)Math.Round(sceneCount * RepresentativeShare), 1, MaximumScreensShown);
        return EmpPrepService.PickSpreadIndexes(sceneCount, count);
    }

    // ── Description ──────────────────────────────────────────────────

    public static string Description(
        IReadOnlyList<PackScene> scenes,
        PackStats stats,
        string title,
        string theme,
        PrepScreenshot? cover,
        EmpPrepSettings settings)
    {
        var shown = ScreensToShow(scenes.Count);
        var screensNote = shown.Count < scenes.Count
            ? $"[i]Screens for {shown.Count} of {scenes.Count} scenes shown here; every scene's screens are in the torrent's {PackLayout.ScreensFolder} folder.[/i]"
            : "";

        var values = new Dictionary<string, string>
        {
            ["cover"] = cover is null ? "" : FullSize(cover),
            ["theme"] = ThemeText(theme),
            ["packtitle"] = title,
            ["count"] = stats.SceneCount.ToString(CultureInfo.InvariantCulture),
            ["packtype"] = PackType(stats.SceneCount),
            ["runtime"] = MetadataBuilder.FormatDuration(stats.TotalDurationSeconds),
            ["size"] = MetadataBuilder.FormatFileSize(stats.TotalSizeBytes),
            ["stats"] = StatsTable(stats),
            ["performers"] = CountedList(stats.Performers, "scene"),
            ["sources"] = Sources(stats),
            ["tags"] = CountedList(stats.Tags, null),
            ["screensnote"] = screensNote,
            ["screens"] = ScreenGrid(shown.Select(index => scenes[index]).ToList(), settings.BbcodeColumns),
        };

        var builder = new StringBuilder(settings.PackDescriptionTemplate);
        foreach (var (key, value) in values)
            builder.Replace($"{{{key}}}", value);

        // A placeholder that rendered empty (no cover, no note) would leave a run of blank lines.
        var text = builder.ToString().Replace("\r\n", "\n");
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim();
    }

    private const string ThemePrompt = "[i]Describe what this pack has in common.[/i]";

    private static string ThemeText(string? theme) => string.IsNullOrWhiteSpace(theme) ? ThemePrompt : theme.Trim();

    /// <summary>
    /// The parts of a pack's description that keep following the theme field, the cover and the
    /// screens after the user edits the description.
    /// </summary>
    /// <param name="previousTheme">The theme before this save, which an edit being saved may still show.</param>
    /// <param name="previousCover">Likewise the cover before this save.</param>
    public static IReadOnlyList<LiveSlot> DescriptionSlots(
        IReadOnlyList<PackScene> scenes,
        string? theme,
        PrepScreenshot? cover,
        EmpPrepSettings settings,
        string? previousTheme = null,
        PrepScreenshot? previousCover = null)
    {
        static PrepScreenshot? NotUploaded(PrepScreenshot? image) => image is null ? null : image with { Url = null, ThumbUrl = null, ViewerUrl = null };

        var shown = ScreensToShow(scenes.Count).Select(index => scenes[index]).ToList();
        var shownNotUploaded = shown.Select(scene => scene with { Sheet = NotUploaded(scene.Sheet) }).ToList();

        return
        [
            new LiveSlot("theme", ThemeText(theme), [ThemeText(previousTheme), ThemePrompt]),
            new LiveSlot("screens", ScreenGrid(shown, settings.BbcodeColumns), [ScreenGrid(shownNotUploaded, settings.BbcodeColumns)]),
            new LiveSlot("cover", cover is null ? "" : FullSize(cover),
                new[] { cover, previousCover }.OfType<PrepScreenshot>()
                    .SelectMany(image => new[] { FullSize(image), FullSize(NotUploaded(image)!) })
                    .ToList()),
        ];
    }

    private static string Percent(int count, int total)
        => total == 0 ? "0%" : $"{Math.Round(count * 100d / total, count * 100 % total == 0 ? 0 : 1).ToString(CultureInfo.InvariantCulture)}%";

    private static string Distribution(IReadOnlyList<CountedValue> values, int total)
        => string.Join(" · ", values.Select(value => $"{value.Name} {Percent(value.Count, total)} ({value.Count})"));

    /// <summary>The pack's numbers as a two-column BBCode table.</summary>
    public static string StatsTable(PackStats stats)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Scenes", stats.SceneCount.ToString(CultureInfo.InvariantCulture)),
            ("Total runtime", MetadataBuilder.FormatDuration(stats.TotalDurationSeconds)),
            ("Total size", MetadataBuilder.FormatFileSize(stats.TotalSizeBytes)),
            ("Quality", Distribution(stats.Qualities, stats.SceneCount)),
            ("Length", stats.SceneCount == 0 ? "" :
                $"shortest {MetadataBuilder.FormatDuration(stats.ShortestSeconds)} · longest {MetadataBuilder.FormatDuration(stats.LongestSeconds)} · "
                + $"average {MetadataBuilder.FormatDuration(stats.AverageSeconds)} · median {MetadataBuilder.FormatDuration(stats.MedianSeconds)}"),
            ("Average file size", MetadataBuilder.FormatFileSize(stats.AverageSizeBytes)),
            ("Video codec", Distribution(stats.Codecs, stats.SceneCount)),
            ("Frame rate", Distribution(stats.FrameRates, stats.SceneCount)),
            ("Released", stats.EarliestDate is { } first && stats.LatestDate is { } last
                ? first == last ? Iso(first) : $"{Iso(first)} – {Iso(last)}"
                : ""),
        };

        var builder = new StringBuilder("[table]\n");
        foreach (var (label, value) in rows.Where(row => !string.IsNullOrWhiteSpace(row.Value) && row.Value != "unknown"))
            builder.Append($"[tr][td][b]{label}[/b][/td][td]{value}[/td][/tr]\n");
        return builder.Append("[/table]").ToString();
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"Name (3 scenes) · Other (1 scene)", or "tag (3) · other (1)" without a unit.</summary>
    private static string CountedList(IReadOnlyList<CountedValue> values, string? unit)
    {
        if (values.Count == 0)
            return "[i]None listed.[/i]";

        return string.Join(" · ", values.Select(value => unit is null
            ? $"{value.Name} ({value.Count})"
            : $"{value.Name} ({value.Count} {unit}{(value.Count == 1 ? "" : "s")})"));
    }

    private static string Sources(PackStats stats)
    {
        var parts = new List<string>();
        if (stats.Studios.Count > 0)
            parts.Add("Studios: " + CountedList(stats.Studios, "scene"));
        if (stats.Websites.Count > 0)
            parts.Add("Sites: " + CountedList(stats.Websites, "scene"));
        return parts.Count == 0 ? "[i]Not recorded.[/i]" : string.Join("\n", parts);
    }

    private static string FullSize(PrepScreenshot image)
        => string.IsNullOrWhiteSpace(image.Url) ? $"[NOT UPLOADED: {image.FileName}]" : $"[img]{image.Url}[/img]";

    /// <summary>
    /// Each shown scene's contact sheet as a clickable thumbnail — the form EMP asks for when a post
    /// carries many screens — laid out in rows.
    /// </summary>
    public static string ScreenGrid(IReadOnlyList<PackScene> scenes, int columns)
    {
        var sheets = scenes.Select(scene => scene.Sheet).ToList();
        if (sheets.Count == 0)
            return "[i]No screens generated yet.[/i]";

        var builder = new StringBuilder();
        for (var index = 0; index < sheets.Count; index++)
        {
            var sheet = sheets[index];
            builder.Append(sheet switch
            {
                null => $"[MISSING SCREENS: {scenes[index].Metadata.Title}]",
                { Url: null or "" } => $"[NOT UPLOADED: {sheet.FileName}]",
                _ => $"[url={sheet.Url}][img]{sheet.ThumbUrl ?? sheet.Url}[/img][/url]",
            });

            var endOfRow = (index + 1) % Math.Max(1, columns) == 0;
            builder.Append(endOfRow || index == sheets.Count - 1 ? "\n" : " ");
        }

        return builder.ToString().TrimEnd();
    }

    // ── Rules ────────────────────────────────────────────────────────

    /// <summary>Check a pack against EMP's pack rules. Errors stop the torrent; warnings are advice.</summary>
    public static IReadOnlyList<PackIssue> Check(IReadOnlyList<PackScene> scenes, string theme, PackStats stats)
    {
        var issues = new List<PackIssue>();

        if (scenes.Count < MegapackMinimumScenes)
        {
            issues.Add(new PackIssue(PackIssue.Warning,
                $"Minipack of {scenes.Count} scenes. EMP discourages small minipacks — for example, 5 scenes of one performer are better "
                + $"uploaded as 5 separate torrents. A megapack starts at {MegapackMinimumScenes} scenes."));
        }

        if (string.IsNullOrWhiteSpace(theme))
        {
            issues.Add(new PackIssue(PackIssue.Warning,
                "Add the pack's theme. EMP requires a text description, and every pack must share a common theme — origin, cast, or fetish."));
        }

        var missingScreens = scenes.Where(scene => scene.Sheet is null).ToList();
        if (missingScreens.Count > 0)
        {
            issues.Add(new PackIssue(PackIssue.Error,
                $"{missingScreens.Count} scene{(missingScreens.Count == 1 ? " has" : "s have")} no screens yet. EMP requires every video in a pack "
                + "to be represented with screens inside the torrent — generate screens for them first."));
        }

        // Two library entries with the same title and performers are very likely the same scene,
        // which EMP forbids in one pack (e.g. a 720p and a 1080p copy).
        var likelyDuplicates = scenes
            .GroupBy(scene => SceneIdentity(scene.Metadata), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.First().Metadata.Title)
            .ToList();
        foreach (var title in likelyDuplicates)
        {
            issues.Add(new PackIssue(PackIssue.Warning,
                $"\"{title}\" appears more than once. EMP does not allow two versions of the same scene in one pack — keep only one."));
        }

        var missingFiles = scenes.Where(scene => string.IsNullOrWhiteSpace(scene.SourcePath)).ToList();
        if (missingFiles.Count > 0)
        {
            issues.Add(new PackIssue(PackIssue.Error,
                $"{missingFiles.Count} scene{(missingFiles.Count == 1 ? " has" : "s have")} no video file in the library."));
        }

        if (stats.Performers.Count > 3 && stats.Studios.Count + stats.Websites.Count > 2)
        {
            issues.Add(new PackIssue(PackIssue.Warning,
                $"This pack spans {stats.Performers.Count} performers and several sources. EMP does not allow random collections — make sure "
                + "the theme says what they share."));
        }

        return issues;
    }

    private static string SceneIdentity(PrepMetadata metadata)
        => $"{metadata.Title.Trim()}|{string.Join(',', metadata.Performers.Order(StringComparer.OrdinalIgnoreCase))}";
}
