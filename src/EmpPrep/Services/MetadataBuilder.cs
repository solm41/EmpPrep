using System.Globalization;
using System.Text;
using System.Text.Json;

using Cove.Core.Entities;

using EmpPrep.Models;

using Microsoft.EntityFrameworkCore;

namespace EmpPrep.Services;

/// <summary>
/// Pulls a video's metadata out of the Cove library and renders the release name, media-info block
/// and BBCode description from the configured templates.
/// </summary>
public sealed class MetadataBuilder(DbContext db, SpriteReader sprites, FfmpegLocator ffmpeg, MediaProbeCache probes)
{
    /// <summary>
    /// Read everything the prep workflow needs about one video, probing its audio if that has not
    /// been done yet.
    /// </summary>
    public async Task<PrepMetadata> DeriveAsync(int videoId, EmpPrepSettings settings, CancellationToken ct = default)
        => (await DeriveManyAsync([videoId], settings, probeAudio: true, ct)).TryGetValue(videoId, out var metadata)
            ? metadata
            : throw new InvalidOperationException($"Video {videoId} is not in the library.");

    /// <summary>
    /// Read many videos at once: one query for the videos and their performers, tags and studio, and
    /// one for their files — instead of several queries per video.
    /// </summary>
    /// <param name="probeAudio">
    /// Start ffprobe for audio details not yet cached. Page loads pass false: probing opens every
    /// file, over the network for a shared library, and made large batches take many seconds to show.
    /// Steps that read the file anyway (generating screens) pass true and fill the cache.
    /// </param>
    /// <returns>Metadata by video id. Videos no longer in the library are absent.</returns>
    public async Task<IReadOnlyDictionary<int, PrepMetadata>> DeriveManyAsync(
        IReadOnlyCollection<int> videoIds,
        EmpPrepSettings settings,
        bool probeAudio,
        CancellationToken ct = default)
    {
        var ids = videoIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, PrepMetadata>();

        // Split query: four collections joined in one statement would multiply rows per video.
        var videos = await db.Set<Video>()
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Studio).ThenInclude(studio => studio!.Urls)
            .Include(candidate => candidate.VideoPerformers).ThenInclude(link => link.Performer)
            .Include(candidate => candidate.VideoTags).ThenInclude(link => link.Tag)
            .Include(candidate => candidate.Urls)
            .Where(candidate => ids.Contains(candidate.Id))
            .ToListAsync(ct);

        var files = await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.VideoId != null && ids.Contains(file.VideoId.Value))
            .ToListAsync(ct);
        var filesByVideo = files.GroupBy(file => file.VideoId!.Value).ToDictionary(group => group.Key, group => group.ToList());

        var result = new Dictionary<int, PrepMetadata>(videos.Count);
        foreach (var video in videos)
        {
            var file = PrimaryFile(video, filesByVideo.GetValueOrDefault(video.Id) ?? []);
            var mediaInfo = await BuildMediaInfoAsync(file, probeAudio, ct);
            result[video.Id] = Compose(video, file, mediaInfo, settings);
        }

        return result;
    }

    /// <summary>
    /// The file the torrent describes: the video's designated primary file, or the largest attached
    /// file when none is marked — so an upload defaults to the best copy.
    /// </summary>
    private static VideoFile? PrimaryFile(Video video, IReadOnlyList<VideoFile> files)
        => files.FirstOrDefault(file => file.Id == video.PrimaryFileId)
            ?? files.OrderByDescending(file => file.Size).FirstOrDefault();

    private PrepMetadata Compose(Video video, VideoFile? file, PrepMediaInfo mediaInfo, EmpPrepSettings settings)
    {
        var videoId = video.Id;

        // Performer order is not stored, so sort by name for a stable, predictable release name.
        var performers = video.VideoPerformers
            .Select(link => link.Performer?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var libraryTags = video.VideoTags
            .Select(link => link.Tag?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var studioWebsite = video.Studio?.Urls
            .Select(url => WebsiteDomain(url.Url))
            .FirstOrDefault(domain => domain is not null);

        // Identifying tags first — performers, studio, quality — then the library's content tags.
        // Order does not matter to the tracker, but it makes the list easy to scan.
        var tagSources = new List<string>();
        if (settings.IncludePerformerTags)
            tagSources.AddRange(performers);
        if (settings.IncludeStudioWebsiteTag && studioWebsite is not null)
            tagSources.Add(studioWebsite);
        if (settings.IncludeStudioTags && !string.IsNullOrWhiteSpace(video.Studio?.Name))
            tagSources.Add(video.Studio!.Name);
        if (settings.IncludeQualityTag && mediaInfo.QualityLabel.Length > 0)
            tagSources.Add(mediaInfo.QualityLabel);
        if (settings.IncludeCodecTag && !string.IsNullOrWhiteSpace(mediaInfo.VideoCodec))
            tagSources.Add(mediaInfo.VideoCodec);
        if (settings.IncludeDateTags && video.Date is { } date)
        {
            tagSources.Add(date.ToString("yyyy", CultureInfo.InvariantCulture));
            tagSources.Add(date.ToString("yyyy.MM", CultureInfo.InvariantCulture));
            tagSources.Add(date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        }
        tagSources.AddRange(libraryTags);

        var excluded = settings.ExcludedTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var formattedTags = EmpTagFormatter.FormatTags(tagSources, settings.LowercaseTags, excluded);

        // Fall back to the file name when the library has no title — better than an empty release.
        var title = !string.IsNullOrWhiteSpace(video.Title)
            ? video.Title!.Trim()
            : Path.GetFileNameWithoutExtension(file?.Path ?? "") is { Length: > 0 } stem
                ? stem
                : $"video-{videoId}";

        var metadata = new PrepMetadata
        {
            VideoId = videoId,
            Title = title,
            Studio = video.Studio?.Name?.Trim(),
            Performers = performers,
            LibraryTags = libraryTags,
            FormattedTags = formattedTags,
            Details = video.Details?.Trim(),
            Date = video.Date,
            Code = video.Code?.Trim(),
            Director = video.Director?.Trim(),
            Urls = video.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).ToList(),
            MediaInfo = mediaInfo,
            HasSprite = sprites.HasSprite(videoId),
            CoverBlobId = string.IsNullOrWhiteSpace(video.ImageBlobId) ? null : video.ImageBlobId,
            StudioWebsite = studioWebsite,
        };

        return metadata with
        {
            ReleaseName = RenderReleaseName(metadata, settings),
            PostTitle = RenderPostTitle(metadata, settings),
        };
    }

    /// <summary>The file the torrent would describe for a video, or null when it has none.</summary>
    public async Task<VideoFile?> ResolvePrimaryFileAsync(int videoId, CancellationToken ct = default)
    {
        var primaryFileId = await db.Set<Video>().AsNoTracking()
            .Where(candidate => candidate.Id == videoId)
            .Select(candidate => candidate.PrimaryFileId)
            .SingleOrDefaultAsync(ct);
        var files = await db.Set<VideoFile>().AsNoTracking().Where(file => file.VideoId == videoId).ToListAsync(ct);
        return files.FirstOrDefault(file => file.Id == primaryFileId) ?? files.OrderByDescending(file => file.Size).FirstOrDefault();
    }

    private async Task<PrepMediaInfo> BuildMediaInfoAsync(VideoFile? file, bool probeAudio, CancellationToken ct)
    {
        if (file is null)
            return new PrepMediaInfo();

        var info = new PrepMediaInfo
        {
            FileName = Path.GetFileName(file.Path),
            FilePath = file.Path,
            FileSizeBytes = file.Size,
            Container = file.Format,
            Width = file.Width,
            Height = file.Height,
            DurationSeconds = file.Duration,
            VideoCodec = file.VideoCodec,
            AudioCodec = file.AudioCodec,
            FrameRate = file.FrameRate,
            BitRateBps = file.BitRate,
        };

        // The scan stores no audio channel/sample-rate detail. It comes from ffprobe, cached per file,
        // and is only probed when asked — a page load uses what is cached and never starts a process.
        var key = MediaProbeCache.Key(file.Path, file.Size, file.ModTime);
        if (!probes.TryGet(key, out var audio))
        {
            if (!probeAudio)
                return info;
            audio = await ProbeAudioAsync(file.Path, ct);
            if (audio is null)
                return info;
            probes.Set(key, audio);
        }

        return info with
        {
            AudioChannels = audio.Channels,
            AudioSampleRate = audio.SampleRate,
            AudioBitRateBps = audio.BitRate,
        };
    }

    /// <summary>Read a file's audio details with ffprobe. Best effort: null when it cannot.</summary>
    private async Task<AudioDetails?> ProbeAudioAsync(string path, CancellationToken ct)
    {
        var ffprobePath = ffmpeg.FfprobePath;
        if (ffprobePath is null || !File.Exists(path))
            return null;

        try
        {
            var (exitCode, stdOut, _) = await FfmpegLocator.RunAsync(
                ffprobePath,
                ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels,sample_rate,bit_rate", "-of", "json", path],
                ct);

            if (exitCode != 0)
                return null;

            using var document = JsonDocument.Parse(stdOut);
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return new AudioDetails(null, null, null);

            var stream = streams[0];
            return new AudioDetails(
                ReadInt(stream, "channels") is { } channels ? (int)channels : null,
                ReadInt(stream, "sample_rate") is { } rate ? (int)rate : null,
                ReadInt(stream, "bit_rate"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static long? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        // ffprobe reports these as JSON strings in some builds and numbers in others.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    // ── Rendering ────────────────────────────────────────────────────

    /// <summary>
    /// The quality label trackers tag with, from the frame size.
    /// </summary>
    /// <remarks>
    /// Uses the shorter side, so a vertical 1080x1920 phone video is "1080p" — keying on height alone
    /// would call it "2160p". Bands follow the conventional ranges (720 up to 1080 is "720p", and so on).
    /// </remarks>
    public static string QualityLabel(int width, int height)
    {
        var side = width > 0 && height > 0 ? Math.Min(width, height) : Math.Max(width, height);
        return side switch
        {
            <= 0 => "",
            < 240 => "144p",
            < 360 => "240p",
            < 480 => "360p",
            < 540 => "480p",
            < 720 => "540p",
            < 1080 => "720p",
            < 1440 => "1080p",
            < 2160 => "1440p",
            < 2880 => "2160p",
            < 4320 => "5K",
            _ => "8K",
        };
    }

    /// <summary>The bare domain of a URL, without "www.", or null when it is not a web URL.</summary>
    internal static string? WebsiteDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var candidate = url.Contains("://", StringComparison.Ordinal) ? url.Trim() : "https://" + url.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return null;

        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>Join performers the way a title reads: "A", "A & B", "A, B & C".</summary>
    public static string JoinPerformers(IReadOnlyList<string> performers) => performers.Count switch
    {
        0 => "",
        1 => performers[0],
        _ => string.Join(", ", performers.Take(performers.Count - 1)) + " & " + performers[^1],
    };

    /// <summary>Render the title for the EMP upload form.</summary>
    public static string RenderPostTitle(PrepMetadata metadata, EmpPrepSettings settings)
    {
        var rendered = ApplyNamePlaceholders(settings.PostTitleTemplate, new Dictionary<string, string>
        {
            ["studio"] = metadata.Studio ?? "",
            ["performers"] = JoinPerformers(metadata.Performers),
            ["title"] = metadata.Title,
            ["date"] = metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            ["quality"] = metadata.MediaInfo.QualityLabel,
            ["resolution"] = metadata.MediaInfo.Resolution,
            ["height"] = metadata.MediaInfo.Height > 0 ? metadata.MediaInfo.Height.ToString(CultureInfo.InvariantCulture) : "",
            ["codec"] = metadata.MediaInfo.VideoCodec,
        });

        return CollapseEmptySegments(rendered);
    }

    public static string RenderReleaseName(PrepMetadata metadata, EmpPrepSettings settings)
    {
        var rendered = ApplyNamePlaceholders(settings.ReleaseNameTemplate, new Dictionary<string, string>
        {
            ["studio"] = metadata.Studio ?? "",
            ["performers"] = string.Join(", ", metadata.Performers),
            ["title"] = metadata.Title,
            ["date"] = metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            ["resolution"] = metadata.MediaInfo.Resolution,
            ["height"] = metadata.MediaInfo.Height > 0 ? metadata.MediaInfo.Height.ToString(CultureInfo.InvariantCulture) : "",
            ["codec"] = metadata.MediaInfo.VideoCodec,
            ["quality"] = metadata.MediaInfo.QualityLabel,
            ["source"] = settings.Source,
        });

        return TorrentBuilder.SanitizeName(CollapseEmptySegments(rendered));
    }

    /// <summary>Render the media-info block shown inside the description.</summary>
    public static string RenderMediaInfoText(PrepMediaInfo info)
    {
        var lines = new List<string>
        {
            $"File      : {info.FileName}",
            $"Size      : {FormatFileSize(info.FileSizeBytes)}",
            $"Container : {Blank(info.Container)}",
            $"Duration  : {FormatDuration(info.DurationSeconds)}",
            $"Resolution: {info.Resolution}",
            $"Video     : {Blank(info.VideoCodec)}{(info.FrameRate > 0 ? $" @ {info.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps" : "")}",
        };

        if (info.BitRateBps > 0)
            lines.Add($"Bitrate   : {FormatBitRate(info.BitRateBps)}");

        var audio = new StringBuilder(Blank(info.AudioCodec));
        if (info.AudioChannels is { } channels)
            audio.Append($", {channels} ch");
        if (info.AudioSampleRate is { } sampleRate)
            audio.Append($", {sampleRate} Hz");
        if (info.AudioBitRateBps is { } audioBitRate and > 0)
            audio.Append($", {FormatBitRate(audioBitRate)}");
        lines.Add($"Audio     : {audio}");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Render the BBCode description.
    /// </summary>
    /// <remarks>
    /// Screenshots that have not been uploaded yet render as a visible
    /// <c>[NOT UPLOADED: filename]</c> marker rather than as a broken <c>[img]</c> tag, so an
    /// upload never silently goes out with dead images in the description.
    /// </remarks>
    public static string RenderDescription(
        PrepMetadata metadata,
        IReadOnlyList<PrepScreenshot> screenshots,
        IReadOnlyList<string> tags,
        EmpPrepSettings settings)
    {
        var mediaInfoText = RenderMediaInfoText(metadata.MediaInfo);

        return ApplyPlaceholders(settings.DescriptionTemplate, new Dictionary<string, string>
        {
            ["title"] = metadata.Title,
            ["studio"] = metadata.Studio ?? "Unknown",
            ["performers"] = metadata.Performers.Count > 0 ? string.Join(", ", metadata.Performers) : "Unknown",
            ["date"] = metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown",
            ["details"] = metadata.Details ?? "",
            ["tags"] = EmpTagFormatter.Join(tags),
            ["mediainfo"] = mediaInfoText,
            ["featured"] = RenderFeaturedImages(screenshots),
            ["screenshots"] = RenderContactSheet(screenshots),
            ["cover"] = CoverUrl(screenshots),
            ["duration"] = FormatDuration(metadata.MediaInfo.DurationSeconds),
            ["resolution"] = metadata.MediaInfo.Resolution,
            ["filename"] = metadata.MediaInfo.FileName,
            ["filesize"] = FormatFileSize(metadata.MediaInfo.FileSizeBytes),
            ["code"] = metadata.Code ?? "",
            ["director"] = metadata.Director ?? "",
            ["releasename"] = metadata.ReleaseName,
            ["posttitle"] = metadata.PostTitle,
            ["quality"] = metadata.MediaInfo.QualityLabel,
        }).Trim();
    }

    private static string CoverUrl(IReadOnlyList<PrepScreenshot> screenshots)
        => screenshots.FirstOrDefault(shot => shot.IsFullSize && !string.IsNullOrWhiteSpace(shot.Url))?.Url ?? "";

    /// <summary>
    /// The images in a video's description, which keep following new screens and uploads after the
    /// user edits the description. Featured comes before cover, whose URL it contains.
    /// </summary>
    public static IReadOnlyList<LiveSlot> DescriptionSlots(IReadOnlyList<PrepScreenshot> screenshots)
    {
        var notUploaded = screenshots.Select(shot => shot with { Url = null, ThumbUrl = null, ViewerUrl = null }).ToList();
        return
        [
            new LiveSlot("featured", RenderFeaturedImages(screenshots), [RenderFeaturedImages(notUploaded)]),
            new LiveSlot("screenshots", RenderContactSheet(screenshots), [RenderContactSheet(notUploaded)]),
            new LiveSlot("cover", CoverUrl(screenshots), []),
        ];
    }

    /// <summary>Render the full-size images, one per line.</summary>
    public static string RenderFeaturedImages(IReadOnlyList<PrepScreenshot> images)
        => string.Join("\n\n", images.Where(image => image.IsFullSize).Select(RenderFullSize));

    /// <summary>
    /// Render the screens section: the contact sheet, full size. The individual frames are not
    /// listed; they are all in the sheet.
    /// </summary>
    public static string RenderContactSheet(IReadOnlyList<PrepScreenshot> images)
    {
        var sheet = images.FirstOrDefault(image => image.Kind == PrepImageKind.ContactSheet);
        return sheet is null ? "[i]No screenshots generated yet.[/i]" : RenderFullSize(sheet);
    }

    private static string RenderFullSize(PrepScreenshot image)
        => string.IsNullOrWhiteSpace(image.Url) ? NotUploaded(image) : $"[img]{image.Url}[/img]";

    /// <summary>
    /// A visible marker for an image that is not hosted yet, instead of a broken [img] tag, so an
    /// upload can never quietly go out with dead images.
    /// </summary>
    private static string NotUploaded(PrepScreenshot image) => $"[NOT UPLOADED: {image.FileName}]";

    private static string ApplyPlaceholders(string template, IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder(template);
        foreach (var (key, value) in values)
            builder.Replace($"{{{key}}}", value);
        return builder.ToString();
    }

    /// <summary>
    /// Render a one-line name template: placeholders filled, and anything a missing value leaves
    /// behind — empty brackets, a dangling " - " — removed.
    /// </summary>
    public static string RenderName(string template, IReadOnlyDictionary<string, string> values)
        => CollapseEmptySegments(ApplyNamePlaceholders(template, values));

    /// <summary>
    /// Fill a one-line name template (a title or release name), dropping the " - " separator that
    /// belonged to any placeholder with no value — so a video with no performers gives
    /// "[Studio] Title", not "[Studio] - Title".
    /// </summary>
    private static string ApplyNamePlaceholders(string template, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                continue;

            var placeholder = $"{{{key}}}";
            template = template.Replace(placeholder + " - ", placeholder).Replace(" - " + placeholder, placeholder);
        }

        return ApplyPlaceholders(template, values);
    }

    /// <summary>
    /// Tidy a template whose placeholders resolved to nothing, so a video with no studio does not
    /// produce a release name starting with " - ".
    /// </summary>
    private static string CollapseEmptySegments(string value)
    {
        var collapsed = value;
        string previous;
        do
        {
            previous = collapsed;
            collapsed = collapsed
                .Replace("  ", " ")
                .Replace("- -", "-")
                .Replace("[]", "")
                .Replace("()", "")
                .Replace("[p]", "")
                .Replace("[ ]", "")
                .Replace("( )", "");
        }
        while (collapsed != previous);

        // A leading "- " is left behind when everything before the dash was empty.
        return collapsed.Trim().Trim('-', ' ').Replace(" - - ", " - ").Trim();
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
            return "unknown";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:D2}m {span.Seconds:D2}s"
            : $"{span.Minutes}m {span.Seconds:D2}s";
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
            return "unknown";

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatBitRate(long bitsPerSecond)
        => bitsPerSecond >= 1_000_000
            ? $"{(bitsPerSecond / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} Mb/s"
            : $"{(bitsPerSecond / 1_000d).ToString("0", CultureInfo.InvariantCulture)} kb/s";
}
