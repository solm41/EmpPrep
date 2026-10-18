using System.Globalization;

using Cove.Core.Interfaces;

using EmpPrep.Models;

using Microsoft.Extensions.Logging;

namespace EmpPrep.Services;

public sealed record ScreenshotFrame(double Seconds, string Path)
{
    /// <summary>Set once the frame has been pushed to an image host.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// Produces the screenshot set for an upload: full-resolution frames spread across the runtime.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does not crop tiles out of Cove's sprite sheet. Sprites are built from 160px
/// frames for scrubbing previews, so a tile is unusable as an upload screenshot — the sheet is the
/// right thing to *pick* timestamps from, and the wrong thing to take pixels from. Frames are
/// therefore re-extracted from the source file at its native resolution, while
/// <see cref="SpriteReader"/> supplies the contact sheet the UI uses for picking.
/// </para>
/// <para>
/// Extraction reads the media file and writes only into the prep output directory; the library file
/// is never modified.
/// </para>
/// </remarks>
public sealed class ScreenshotService(
    FfmpegLocator ffmpeg,
    CoveConfiguration config,
    ILogger<ScreenshotService> logger)
{
    /// <summary>
    /// One limit on concurrent ffmpeg processes for the whole extension, shared by every job.
    /// </summary>
    /// <remarks>
    /// A per-call limit multiplied with the number of jobs: 130 scene jobs of four processes each put
    /// hundreds of ffmpeg decodes on the network share at once and starved Cove itself. The service
    /// is a singleton, so this caps the total no matter how many jobs are running.
    /// </remarks>
    private readonly SemaphoreSlim _ffmpegSlots = new(
        Math.Clamp(config.MaxParallelTasks / 2, 1, 4),
        Math.Clamp(config.MaxParallelTasks / 2, 1, 4));

    /// <summary>Run <paramref name="work"/> while holding one of the shared ffmpeg slots.</summary>
    private async Task<T> WithFfmpegSlotAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _ffmpegSlots.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            _ffmpegSlots.Release();
        }
    }

    /// <summary>
    /// Choose the timestamps to capture.
    /// </summary>
    /// <remarks>
    /// One frame per <see cref="EmpPrepSettings.SecondsPerFrame"/>, but never fewer than
    /// <see cref="EmpPrepSettings.MinFrameCount"/> — so a 10-minute clip still gets a full grid
    /// rather than seven frames. The floor yields only for a video too short to space that many
    /// frames at least <see cref="EmpPrepSettings.MinFrameSpacingSeconds"/> apart, which is what
    /// stops a 30-second clip from producing twelve near-identical stills.
    /// </remarks>
    public static IReadOnlyList<double> ChooseTimestamps(double durationSeconds, EmpPrepSettings settings)
    {
        if (durationSeconds <= 0)
            return [];

        var start = durationSeconds * (settings.SkipStartPercent / 100d);
        var end = durationSeconds * (1 - settings.SkipEndPercent / 100d);
        var usable = end - start;

        // A clip shorter than the trim margins themselves: use the whole thing.
        if (usable <= 0)
        {
            start = 0;
            end = durationSeconds;
            usable = durationSeconds;
        }

        var bySpacing = (int)Math.Ceiling(usable / settings.SecondsPerFrame);
        var desired = Math.Max(settings.MinFrameCount, bySpacing);

        // "Unless it is really short": cap the count so frames stay meaningfully apart.
        var maxBySpacing = (int)Math.Floor(usable / settings.MinFrameSpacingSeconds);
        var count = Math.Clamp(Math.Min(desired, Math.Max(1, maxBySpacing)), 1, settings.MaxFrameCount);

        var step = usable / count;
        var timestamps = new double[count];
        for (var index = 0; index < count; index++)
        {
            // Centre of each slice, so the first frame is not the trim boundary itself.
            timestamps[index] = Math.Round(start + step * (index + 0.5), 3);
        }

        return timestamps;
    }

    /// <summary>
    /// Extract frames from <paramref name="mediaPath"/> at the given timestamps into
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    public async Task<IReadOnlyList<ScreenshotFrame>> ExtractAsync(
        string mediaPath,
        string outputDirectory,
        IReadOnlyList<double> timestamps,
        EmpPrepSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException($"The video file is not on disk: {mediaPath}", mediaPath);
        if (timestamps.Count == 0)
            return [];

        var ffmpegPath = ffmpeg.RequireFfmpeg();
        Directory.CreateDirectory(outputDirectory);

        var extension = settings.ScreenshotFormat == "png" ? "png" : "jpg";
        var results = new ScreenshotFrame?[timestamps.Count];

        // A few concurrent ffmpeg seeks are much faster than serial extraction; the shared slots keep
        // the total across every running job within budget.
        var completed = 0;

        var extractions = timestamps.Select(async (seconds, index) =>
        {
            var fileName = $"{index + 1:D2}_{FormatTimestampForFileName(seconds)}.{extension}";
            var outputPath = Path.Combine(outputDirectory, fileName);

            if (await WithFfmpegSlotAsync(() => ExtractFrameAsync(ffmpegPath, mediaPath, outputPath, seconds, settings, ct), ct))
                results[index] = new ScreenshotFrame(seconds, outputPath);

            progress?.Report((double)Interlocked.Increment(ref completed) / timestamps.Count);
        });

        await Task.WhenAll(extractions);

        var frames = results.Where(frame => frame is not null).Select(frame => frame!).ToList();
        if (frames.Count == 0)
            throw new InvalidOperationException(
                $"ffmpeg produced no frames for {Path.GetFileName(mediaPath)}. The file may be unreadable or use a codec this ffmpeg build cannot decode.");

        if (frames.Count < timestamps.Count)
        {
            logger.LogWarning(
                "EMP Prep extracted {Extracted} of {Requested} frames for {Path}; some seeks failed.",
                frames.Count, timestamps.Count, mediaPath);
        }

        return frames;
    }

    private async Task<bool> ExtractFrameAsync(
        string ffmpegPath,
        string mediaPath,
        string outputPath,
        double seconds,
        EmpPrepSettings settings,
        CancellationToken ct)
    {
        // -ss before -i: ffmpeg seeks to the preceding keyframe and decodes forward to the exact
        // timestamp, which is both accurate and far cheaper than decoding from the start.
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", mediaPath,
            "-frames:v", "1",
        };

        if (settings.ScreenshotWidth > 0)
        {
            // -2 keeps the aspect ratio and an even height, which JPEG/H.264 chroma requires.
            arguments.AddRange(["-vf", $"scale={settings.ScreenshotWidth}:-2"]);
        }

        if (settings.ScreenshotFormat == "png")
        {
            arguments.AddRange(["-c:v", "png"]);
        }
        else
        {
            // ffmpeg's mjpeg -q:v runs 2 (best) to 31 (worst); map the 1-100 quality setting onto it.
            var quality = 2 + (int)Math.Round((100 - settings.JpegQuality) / 100d * 29);
            arguments.AddRange(["-q:v", Math.Clamp(quality, 2, 31).ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-y", outputPath]);

        var (exitCode, _, stdErr) = await FfmpegLocator.RunAsync(ffmpegPath, arguments, ct);

        if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return true;

        logger.LogDebug(
            "EMP Prep frame extraction failed at {Seconds}s for {Path} (exit {ExitCode}): {Error}",
            seconds, mediaPath, exitCode, stdErr.Trim());

        // A zero-byte leftover would otherwise be picked up as a valid screenshot.
        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort */ }
        return false;
    }

    /// <summary>
    /// Compose every extracted frame into a single grid image.
    /// </summary>
    /// <remarks>
    /// Uses ffmpeg's <c>tile</c> filter over the frames as an image sequence. The frames are named
    /// by timestamp, which ffmpeg cannot read as a sequence, so they are first copied into a scratch
    /// directory under sequential names; the scratch copies are deleted afterwards. The source frames
    /// and the library file are not touched.
    /// </remarks>
    public async Task<string?> BuildContactSheetAsync(
        IReadOnlyList<ScreenshotFrame> frames,
        string outputPath,
        EmpPrepSettings settings,
        CancellationToken ct = default,
        string? header = null)
    {
        if (frames.Count < 2)
            return null;

        var ffmpegPath = ffmpeg.RequireFfmpeg();
        var columns = Math.Min(settings.ContactSheetColumns, frames.Count);
        var rows = (int)Math.Ceiling(frames.Count / (double)columns);
        var scratch = Path.Combine(Path.GetDirectoryName(outputPath)!, $".sheet-{Guid.NewGuid():N}");

        Directory.CreateDirectory(scratch);
        try
        {
            // Each frame is resized to its tile (and stamped with its timestamp) into the scratch
            // directory under a sequential name, which is what ffmpeg needs to read them as one
            // image sequence. The source frames are only read.
            const string extension = ".jpg";
            var fontFile = FindFontFile();
            var stamped = 0;

            // Tiles are small, independent ffmpeg runs: do them side by side within the shared slots.
            var tiles = frames.Select((frame, index) => WithFfmpegSlotAsync(
                () => WriteTileAsync(ffmpegPath, frame, Path.Combine(scratch, $"{index + 1:D3}{extension}"), settings, fontFile, ct),
                ct));
            stamped = (await Task.WhenAll(tiles)).Count(drawn => drawn);

            if (settings.ContactSheetTimestamps && stamped < frames.Count)
            {
                logger.LogWarning(
                    "EMP Prep stamped timestamps on {Stamped} of {Total} contact-sheet tiles (font: {Font}); the rest are unlabelled.",
                    stamped, frames.Count, fontFile ?? "none found");
            }

            // Tiles are already sized; lay them out. An incomplete last row is padded.
            var layout = $"tile={columns}x{rows}:padding=4:margin=4";

            // With a header, add a band across the top naming the file and its resolution, so each
            // sheet says which video it shows. If drawing it fails, the sheet is built without it.
            if (!string.IsNullOrWhiteSpace(header) && settings.ContactSheetHeader)
            {
                var sheetWidth = columns * settings.ContactSheetTileWidth + (columns + 1) * 4;
                var headerFilter = HeaderFilter(header, sheetWidth, fontFile, Path.Combine(scratch, "header.txt"));
                if (await RunSheetAsync(ffmpegPath, scratch, extension, layout + "," + headerFilter, outputPath, ct) is null)
                    return outputPath;
                logger.LogWarning("EMP Prep could not draw the contact sheet header; building the sheet without it.");
            }

            var error = await RunSheetAsync(ffmpegPath, scratch, extension, layout, outputPath, ct);
            if (error is null)
                return outputPath;

            logger.LogWarning("EMP Prep contact sheet failed: {Error}", error);
            return null;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Tile the scratch frames into the sheet. Null on success, otherwise ffmpeg's error.</summary>
    private static async Task<string?> RunSheetAsync(
        string ffmpegPath, string scratch, string extension, string filter, string outputPath, CancellationToken ct)
    {
        var (exitCode, _, stdErr) = await FfmpegLocator.RunAsync(
            ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-framerate", "1", "-start_number", "1",
                "-i", Path.Combine(scratch, $"%03d{extension}"),
                "-vf", filter,
                "-frames:v", "1", "-q:v", "3",
                "-y", outputPath,
            ],
            ct);

        return exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
            ? null
            : $"exit {exitCode}: {stdErr.Trim()}";
    }

    /// <summary>
    /// The text across the top of a contact sheet: file name, resolution and the essentials, so the
    /// sheet says which video it is when seen on its own — in the Screens folder or in a post.
    /// </summary>
    public static string SheetHeader(PrepMediaInfo info)
    {
        var parts = new List<string> { info.FileName };
        if (info.Width > 0 && info.Height > 0)
        {
            var quality = PackComposer.QualityName(info.QualityLabel);
            parts.Add(quality.Length > 0 ? $"{info.Width}x{info.Height} ({quality})" : $"{info.Width}x{info.Height}");
        }
        if (!string.IsNullOrWhiteSpace(info.VideoCodec))
            parts.Add(info.FrameRate > 0
                ? $"{info.VideoCodec} {Math.Round(info.FrameRate, 2).ToString(CultureInfo.InvariantCulture)} fps"
                : info.VideoCodec);
        if (info.DurationSeconds > 0)
            parts.Add(MetadataBuilder.FormatDuration(info.DurationSeconds));
        if (info.FileSizeBytes > 0)
            parts.Add(MetadataBuilder.FormatFileSize(info.FileSizeBytes));
        return string.Join("  ·  ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// A band across the top of the sheet with <paramref name="header"/> in it.
    /// </summary>
    /// <remarks>
    /// The text goes through a file rather than the filter string: file names contain quotes, colons,
    /// commas and percent signs, every one of which means something in an ffmpeg filter. Reading it
    /// from a file with expansion off draws it exactly as written. Long names are shortened in the
    /// middle so the resolution at the end stays visible.
    /// </remarks>
    internal static string HeaderFilter(string header, int sheetWidth, string? fontFile, string textFilePath)
    {
        var bandHeight = Math.Clamp(sheetWidth / 32, 40, 96);
        var fontSize = (int)(bandHeight * 0.5);
        // Bold sans averages about half its size per character for file-name text (measured on a real
        // sheet); a little extra keeps runs of wide letters from running off the edge.
        var maxCharacters = Math.Max(20, (int)((sheetWidth - 28) / (fontSize * 0.52)));

        File.WriteAllText(textFilePath, FitHeader(header, maxCharacters), new System.Text.UTF8Encoding(false));

        var font = fontFile is null ? "" : $"fontfile='{EscapeFilterValue(fontFile.Replace('\\', '/'))}':";
        return $"pad=iw:ih+{bandHeight}:0:{bandHeight}:color=black,"
            + $"drawtext={font}textfile='{EscapeFilterValue(textFilePath.Replace('\\', '/'))}':expansion=none"
            + $":fontcolor=white:fontsize={fontSize}:x=14:y=({bandHeight}-th)/2";
    }

    /// <summary>Shorten text to fit, cutting the middle of the file name rather than the details after it.</summary>
    internal static string FitHeader(string header, int maxCharacters)
    {
        if (header.Length <= maxCharacters)
            return header;

        var split = header.IndexOf("  ·  ", StringComparison.Ordinal);
        var name = split < 0 ? header : header[..split];
        var details = split < 0 ? "" : header[split..];
        var room = maxCharacters - details.Length - 1;

        if (room < 12)
            return header[..(maxCharacters - 1)] + "…";

        var head = (room + 1) / 2;
        return name[..head] + "…" + name[^(room - head)..] + details;
    }

    /// <summary>
    /// Write one contact-sheet tile: the frame scaled to the tile width, with its timestamp in the
    /// bottom-right corner so viewers can tell where in the video each shot is from.
    /// </summary>
    /// <returns>True when the timestamp was drawn.</returns>
    /// <remarks>
    /// If drawing the text fails — typically no usable font — the tile is written without it rather
    /// than dropping the frame, so a font problem costs the labels, never the sheet.
    /// </remarks>
    private async Task<bool> WriteTileAsync(
        string ffmpegPath,
        ScreenshotFrame frame,
        string tilePath,
        EmpPrepSettings settings,
        string? fontFile,
        CancellationToken ct)
    {
        var scale = $"scale={settings.ContactSheetTileWidth}:-2";

        if (settings.ContactSheetTimestamps)
        {
            var filter = scale + "," + TimestampFilter(frame.Seconds, fontFile);
            if (await RunTileAsync(ffmpegPath, frame.Path, tilePath, filter, ct))
                return true;
        }

        if (!await RunTileAsync(ffmpegPath, frame.Path, tilePath, scale, ct))
            File.Copy(frame.Path, tilePath, overwrite: true);
        return false;
    }

    private static async Task<bool> RunTileAsync(string ffmpegPath, string source, string target, string filter, CancellationToken ct)
    {
        var (exitCode, _, _) = await FfmpegLocator.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-vf", filter, "-q:v", "2", "-y", target],
            ct);
        return exitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
    }

    /// <summary>
    /// The drawtext filter for a tile's timestamp: white text on a translucent dark box in the
    /// bottom-right corner, sized relative to the tile so it stays legible at any tile width.
    /// </summary>
    internal static string TimestampFilter(double seconds, string? fontFile)
    {
        var font = fontFile is null ? "" : $"fontfile='{EscapeFilterValue(fontFile.Replace('\\', '/'))}':";
        return $"drawtext={font}text='{EscapeFilterValue(FormatTimestamp(seconds))}'"
            + ":fontcolor=white:fontsize=h*0.075:box=1:boxcolor=black@0.6:boxborderw=5"
            + ":x=w-tw-10:y=h-th-10";
    }

    /// <summary>"1:22", or "1:02:05" past an hour — the same form the page shows.</summary>
    internal static string FormatTimestamp(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// Escape a value for use inside a quoted ffmpeg filter option. A colon separates options even
    /// inside the quotes at the filter level, so it has to be backslash-escaped — which is why a
    /// timestamp such as 1:22, or a Windows font path such as C:/..., would otherwise break the filter.
    /// </summary>
    internal static string EscapeFilterValue(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:");

    /// <summary>
    /// A font file drawtext can use. Windows ffmpeg builds often cannot find a font by name, so an
    /// explicit file is supplied; null lets ffmpeg's own font lookup try.
    /// </summary>
    private static string? FindFontFile()
    {
        var windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        [
            Path.Combine(windowsFonts, "arialbd.ttf"),
            Path.Combine(windowsFonts, "segoeuib.ttf"),
            Path.Combine(windowsFonts, "arial.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
            "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
            "/Library/Fonts/Arial Bold.ttf",
        ];

        return candidates.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && File.Exists(candidate));
    }

    private static string FormatTimestampForFileName(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours:D2}h{span.Minutes:D2}m{span.Seconds:D2}s";
    }
}
