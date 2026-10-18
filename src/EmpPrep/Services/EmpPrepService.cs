using System.Text.Json;

using Cove.Core.Entities;
using Cove.Core.Interfaces;

using EmpPrep.Data;
using EmpPrep.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmpPrep.Services;

/// <summary>Raised for problems the user can fix; surfaced as a 400 rather than a 500.</summary>
public sealed class EmpPrepValidationException(string message) : Exception(message);

public sealed record BatchSummary(Guid Id, string Name, int ItemCount, int ReadyCount, DateTime UpdatedAtUtc);

public sealed record BatchDetail(Guid Id, string Name, DateTime UpdatedAtUtc, IReadOnlyList<PrepPackage> Items)
{
    /// <summary>The pack post and torrent, when the batch holds more than one video.</summary>
    public PackPackage? Pack { get; init; }
}

public sealed record ItemOverrides(string? Title, string? Tags, string? Description, string? ReleaseName)
{
    /// <summary>The EMP upload form's title. Added after the positional fields, so it is optional.</summary>
    public string? PostTitle { get; init; }
}

/// <summary>
/// The prep workflow: queue videos, derive their metadata, extract screenshots, host them, and
/// build a private torrent from the file where it already sits.
/// </summary>
/// <remarks>
/// Every method takes the acting user's id explicitly rather than reading the ambient principal,
/// because the long-running steps run inside host jobs that outlive the request that started them
/// and therefore have no principal to read.
/// </remarks>
public sealed class EmpPrepService(
    DbContext db,
    MetadataBuilder metadata,
    ScreenshotService screenshots,
    SpriteReader sprites,
    HamsterImgUploader imageHost,
    IBlobService blobs,
    CoveConfiguration config,
    EmpPrepSettingsProvider settingsProvider,
    ILogger<EmpPrepService> logger)
{
    private const string DefaultBatchName = "Unsorted";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Batches ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BatchSummary>> ListBatchesAsync(int userId, CancellationToken ct = default)
    {
        var batches = await db.Set<PrepBatch>()
            .AsNoTracking()
            .Where(batch => batch.UserId == userId)
            .OrderByDescending(batch => batch.UpdatedAtUtc)
            .ToListAsync(ct);

        var ids = batches.Select(batch => batch.Id).ToList();

        var counts = await db.Set<PrepItem>()
            .AsNoTracking()
            .Where(item => ids.Contains(item.BatchId))
            .GroupBy(item => item.BatchId)
            .Select(group => new
            {
                BatchId = group.Key,
                Total = group.Count(),
                Ready = group.Count(item => item.Status == PrepItemStatus.Ready),
            })
            .ToListAsync(ct);

        return batches.Select(batch =>
        {
            var count = counts.FirstOrDefault(entry => entry.BatchId == batch.Id);
            return new BatchSummary(batch.Id, batch.Name, count?.Total ?? 0, count?.Ready ?? 0, batch.UpdatedAtUtc);
        }).ToList();
    }

    public async Task<PrepBatch> CreateBatchAsync(int userId, string? name, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var batch = new PrepBatch
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = NormalizeBatchName(name),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Set<PrepBatch>().Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task DeleteBatchAsync(int userId, Guid batchId, CancellationToken ct = default)
    {
        var batch = await RequireBatchAsync(userId, batchId, ct);
        db.Set<PrepBatch>().Remove(batch);
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameBatchAsync(int userId, Guid batchId, string name, CancellationToken ct = default)
    {
        var batch = await RequireBatchAsync(userId, batchId, ct);
        batch.Name = NormalizeBatchName(name);
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Queue videos for prep. This is what the library's bulk and context-menu actions call.
    /// </summary>
    /// <remarks>
    /// Videos already in the target batch are skipped rather than rejected, so re-running the action
    /// over an overlapping selection is harmless and the caller is told how many were new.
    /// </remarks>
    public async Task<(Guid BatchId, string BatchName, int Added, int Skipped)> AddVideosAsync(
        int userId,
        Guid? batchId,
        string? newBatchName,
        IReadOnlyList<int> videoIds,
        CancellationToken ct = default)
    {
        var distinctIds = videoIds.Distinct().Where(id => id > 0).ToList();
        if (distinctIds.Count == 0)
            throw new EmpPrepValidationException("No videos were selected.");

        // Only queue ids that actually exist: a stale selection would otherwise fail on the
        // foreign key with an error that says nothing useful.
        var known = await db.Set<Video>()
            .AsNoTracking()
            .Where(video => distinctIds.Contains(video.Id))
            .Select(video => video.Id)
            .ToListAsync(ct);

        if (known.Count == 0)
            throw new EmpPrepValidationException("None of the selected videos are in the library any more.");

        // Each selection sent from the library becomes its own batch: several videos form one pack,
        // a single video one upload. Appending to whatever batch was used last would silently merge
        // unrelated selections into one torrent.
        var batch = batchId is { } existingId
            ? await RequireBatchAsync(userId, existingId, ct)
            : await CreateBatchAsync(userId, newBatchName ?? await DefaultBatchNameAsync(known, ct), ct);

        var alreadyQueued = await db.Set<PrepItem>()
            .AsNoTracking()
            .Where(item => item.BatchId == batch.Id)
            .Select(item => item.VideoId)
            .ToListAsync(ct);

        var nextPosition = alreadyQueued.Count == 0
            ? 0
            : await db.Set<PrepItem>().Where(item => item.BatchId == batch.Id).MaxAsync(item => item.Position, ct) + 1;

        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var videoId in known)
        {
            if (alreadyQueued.Contains(videoId))
                continue;

            db.Set<PrepItem>().Add(new PrepItem
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                VideoId = videoId,
                Position = nextPosition++,
                AddedAtUtc = now,
                UpdatedAtUtc = now,
                Status = PrepItemStatus.Pending,
            });
            added++;
        }

        batch.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        return (batch.Id, batch.Name, added, distinctIds.Count - added);
    }

    /// <summary>Name a new batch after its video, or as a pack of however many it holds.</summary>
    private async Task<string> DefaultBatchNameAsync(IReadOnlyList<int> videoIds, CancellationToken ct)
    {
        if (videoIds.Count != 1)
            return $"Pack of {videoIds.Count} videos";

        var title = await db.Set<Video>().AsNoTracking()
            .Where(video => video.Id == videoIds[0])
            .Select(video => video.Title)
            .SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(title) ? $"Video {videoIds[0]}" : title.Trim();
    }

    public async Task RemoveItemAsync(int userId, Guid itemId, CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        db.Set<PrepItem>().Remove(item);
        await db.SaveChangesAsync(ct);
    }

    // ── Reading ──────────────────────────────────────────────────────

    public async Task<BatchDetail> GetBatchAsync(int userId, Guid batchId, CancellationToken ct = default)
    {
        var batch = await RequireBatchAsync(userId, batchId, ct);
        var settings = await settingsProvider.GetAsync(ct);

        var items = await db.Set<PrepItem>()
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .OrderBy(item => item.Position)
            .ToListAsync(ct);

        // One batched read for every video, shared by the scene cards and the pack. A single video
        // may be probed for audio detail; a big batch uses only what is cached, so opening it never
        // starts a process per video.
        var derived = await metadata.DeriveManyAsync(
            items.Select(item => item.VideoId).ToList(), settings, probeAudio: items.Count == 1, ct);

        var packages = items.Select(item => AssemblePackage(item, derived.GetValueOrDefault(item.VideoId), settings)).ToList();

        var pack = items.Count > 1
            ? AssemblePack(batch, ScenesFrom(items, derived), settings)
            : null;

        return new BatchDetail(batch.Id, batch.Name, batch.UpdatedAtUtc, packages) { Pack = pack };
    }

    public async Task<PrepPackage> GetItemAsync(int userId, Guid itemId, CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct, tracking: false);
        return await AssemblePackageAsync(item, await settingsProvider.GetAsync(ct), ct);
    }

    /// <summary>
    /// Combine the library's metadata with the user's edits into the copy-ready package.
    /// </summary>
    /// <remarks>
    /// Derivation is re-run on read rather than cached, so editing tags or the title in Cove is
    /// reflected in the prep package without having to re-queue the video. A video whose file has
    /// gone missing yields a package carrying the error instead of throwing, so one broken item
    /// cannot make the whole batch unreadable.
    /// </remarks>
    private async Task<PrepPackage> AssemblePackageAsync(PrepItem item, EmpPrepSettings settings, CancellationToken ct)
    {
        PrepMetadata? derived;
        try
        {
            derived = await metadata.DeriveAsync(item.VideoId, settings, ct);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogWarning(exception, "EMP Prep could not derive metadata for video {VideoId}.", item.VideoId);
            derived = null;
        }

        return AssemblePackage(item, derived, settings);
    }

    /// <summary>Assemble a package from already-derived metadata, or an error package without it.</summary>
    private static PrepPackage AssemblePackage(PrepItem item, PrepMetadata? derived, EmpPrepSettings settings)
    {
        if (derived is null)
        {
            return new PrepPackage
            {
                ItemId = item.Id,
                VideoId = item.VideoId,
                Status = PrepItemStatus.Error,
                LastError = "This video is no longer in the library, or its details could not be read.",
            };
        }

        var shots = DeserializeScreenshots(item.ScreenshotsJson);

        var tags = string.IsNullOrWhiteSpace(item.TagsOverride)
            ? derived.FormattedTags
            : EmpTagFormatter.ParseUserTags(item.TagsOverride, settings.LowercaseTags);

        var title = string.IsNullOrWhiteSpace(item.TitleOverride) ? derived.Title : item.TitleOverride!;
        var effective = derived with { Title = title };
        // An edited scene title flows into the generated post title unless that was edited too.
        var postTitle = string.IsNullOrWhiteSpace(item.PostTitleOverride)
            ? MetadataBuilder.RenderPostTitle(effective, settings)
            : item.PostTitleOverride!;
        effective = effective with { PostTitle = postTitle };

        var description = string.IsNullOrWhiteSpace(item.DescriptionOverride)
            ? MetadataBuilder.RenderDescription(effective, shots, tags, settings)
            : LiveDescription.Fill(item.DescriptionOverride!, MetadataBuilder.DescriptionSlots(shots));

        return new PrepPackage
        {
            ItemId = item.Id,
            VideoId = item.VideoId,
            Status = item.Status,
            ReleaseName = item.ReleaseName ?? derived.ReleaseName,
            Title = title,
            PostTitle = postTitle,
            EditedFields = new (string Name, string? Value)[]
                {
                    ("title", item.TitleOverride),
                    ("postTitle", item.PostTitleOverride),
                    ("tags", item.TagsOverride),
                    ("description", item.DescriptionOverride),
                }
                .Where(field => !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => field.Name)
                .ToList(),
            Tags = tags,
            TagString = EmpTagFormatter.Join(tags),
            Studio = derived.Studio,
            Performers = derived.Performers,
            Description = description,
            MediaInfoText = MetadataBuilder.RenderMediaInfoText(derived.MediaInfo),
            Screenshots = shots,
            CoverImageUrl = shots.FirstOrDefault(shot => shot.IsFullSize && !string.IsNullOrWhiteSpace(shot.Url))?.Url,
            HasLibraryCover = derived.CoverBlobId is not null,
            TorrentPath = item.TorrentPath,
            InfoHash = item.InfoHash,
            TorrentTotalBytes = item.TorrentTotalBytes,
            TorrentPieceLength = item.TorrentPieceLength,
            TorrentPieceCount = item.TorrentPieceCount,
            MediaPath = derived.MediaInfo.FilePath,
            LastError = item.LastError,
            Derived = derived,
        };
    }

    public Task<SpriteSheetInfo> GetSpriteAsync(int videoId, CancellationToken ct = default)
        => sprites.ReadAsync(videoId, ct);

    /// <summary>The timestamps a prep run would capture, for previewing before extraction.</summary>
    public async Task<IReadOnlyList<double>> PlanTimestampsAsync(int videoId, CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetAsync(ct);
        var file = await metadata.ResolvePrimaryFileAsync(videoId, ct);
        return file is null ? [] : ScreenshotService.ChooseTimestamps(file.Duration, settings);
    }

    // ── Editing ──────────────────────────────────────────────────────

    public async Task<PrepPackage> UpdateItemAsync(
        int userId,
        Guid itemId,
        ItemOverrides overrides,
        CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        var settings = await settingsProvider.GetAsync(ct);
        var derived = await metadata.DeriveAsync(item.VideoId, settings, ct);
        var shots = DeserializeScreenshots(item.ScreenshotsJson);

        var edits = StoredEdits(derived, shots, overrides, settings);
        item.TitleOverride = edits.Title;
        item.PostTitleOverride = edits.PostTitle;
        item.TagsOverride = edits.Tags;
        item.DescriptionOverride = edits.Description;

        if (Blank(overrides.ReleaseName) is { } releaseName)
            item.ReleaseName = TorrentBuilder.SanitizeName(releaseName);

        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await AssemblePackageAsync(item, settings, ct);
    }

    // ── Screenshots ──────────────────────────────────────────────────

    /// <summary>
    /// Extract the screenshot set for one item.
    /// </summary>
    /// <param name="timestamps">
    /// Explicit timestamps, or null to use the automatic spacing rule.
    /// </param>
    public async Task<PrepPackage> GenerateScreenshotsAsync(
        int userId,
        Guid itemId,
        IReadOnlyList<double>? timestamps = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        var settings = await settingsProvider.GetAsync(ct);

        try
        {
            var derived = await metadata.DeriveAsync(item.VideoId, settings, ct);
            var mediaPath = derived.MediaInfo.FilePath;

            if (string.IsNullOrWhiteSpace(mediaPath))
                throw new EmpPrepValidationException("This video has no file attached in the library.");

            var chosen = timestamps is { Count: > 0 }
                ? timestamps.Where(seconds => seconds >= 0).Distinct().Order().ToList()
                : ScreenshotService.ChooseTimestamps(derived.MediaInfo.DurationSeconds, settings);

            if (chosen.Count == 0)
                throw new EmpPrepValidationException(
                    "The library has no duration for this video, so screenshot timestamps cannot be planned. Re-scan it, or pass explicit timestamps.");

            var itemDirectory = ResolveItemDirectory(settings, item, derived);
            var screenshotDirectory = Path.Combine(itemDirectory, "screens");

            // Drop a previous set so a re-run does not leave frames from old timestamps behind and
            // silently include them in the description.
            if (Directory.Exists(screenshotDirectory))
                Directory.Delete(screenshotDirectory, recursive: true);

            // Frames are most of the work; the featured images after them are quick.
            var frameProgress = progress is null
                ? null
                : new Progress<double>(fraction => progress.Report(fraction * 0.9));
            var frames = await screenshots.ExtractAsync(
                mediaPath, screenshotDirectory, chosen, settings, frameProgress, ct);

            var images = await BuildImageSetAsync(derived, frames, screenshotDirectory, settings, ct);
            progress?.Report(1);

            item.ScreenshotsJson = SerializeScreenshots(images);
            item.ReleaseName ??= derived.ReleaseName;
            item.Status = item.TorrentPath is null ? PrepItemStatus.Screens : PrepItemStatus.Ready;
            item.LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Status = PrepItemStatus.Error;
            item.LastError = exception.Message;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await AssemblePackageAsync(item, settings, ct);
    }

    /// <summary>
    /// Generate screens for a batch's videos in one job — the missing ones, or all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Videos are processed one after another; within each, frames are extracted in parallel
    /// within the shared ffmpeg limit. Scheduling a job per video instead started every job at once
    /// on a large pack, which flooded the share with ffmpeg processes and left Cove unresponsive.
    /// </para>
    /// <para>
    /// Each video's result is saved as soon as it is done, so progress survives a cancelled or
    /// failed run, and one bad file does not stop the rest: failures are collected and reported
    /// together at the end.
    /// </para>
    /// </remarks>
    public async Task GenerateBatchScreensAsync(
        int userId,
        Guid batchId,
        bool missingOnly,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await RequireBatchAsync(userId, batchId, ct);

        var items = await db.Set<PrepItem>()
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .OrderBy(item => item.Position)
            .Select(item => new { item.Id, item.VideoId, item.ScreenshotsJson })
            .ToListAsync(ct);

        var todo = items
            .Where(item => !missingOnly
                || !DeserializeScreenshots(item.ScreenshotsJson).Any(shot => shot.Kind == PrepImageKind.ContactSheet))
            .ToList();

        var failures = new List<string>();
        for (var index = 0; index < todo.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var done = index;
            var itemProgress = progress is null
                ? null
                : new Progress<double>(fraction => progress.Report((done + fraction) / todo.Count));

            try
            {
                await GenerateScreenshotsAsync(userId, todo[index].Id, null, itemProgress, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"video {todo[index].VideoId}: {exception.Message}");
            }
            finally
            {
                // One context serves the whole run; don't let it accumulate every item it saved.
                db.ChangeTracker.Clear();
            }

            progress?.Report((double)(index + 1) / todo.Count);
        }

        if (failures.Count > 0)
        {
            throw new EmpPrepValidationException(
                $"Screens failed for {failures.Count} of {todo.Count} videos (the rest were saved): "
                + string.Join("; ", failures.Take(5)) + (failures.Count > 5 ? $"; and {failures.Count - 5} more" : ""));
        }
    }

    /// <summary>
    /// Assemble an item's images: a few full-size images, the contact sheet, and the frames it is made from.
    /// </summary>
    /// <remarks>
    /// Only the full-size images and the contact sheet are uploaded and appear in the description.
    /// The individual frames stay on disk: they are what the contact sheet is built from, and any one
    /// of them can be starred to become a full-size image instead.
    ///
    /// The full-size set is the library cover (when enabled and present) topped up with frames to
    /// <see cref="EmpPrepSettings.FullSizeImageCount"/>, spread evenly through the video so they
    /// show different moments.
    /// </remarks>
    private async Task<List<PrepScreenshot>> BuildImageSetAsync(
        PrepMetadata derived,
        IReadOnlyList<ScreenshotFrame> frames,
        string screenshotDirectory,
        EmpPrepSettings settings,
        CancellationToken ct)
    {
        var leading = new List<PrepScreenshot>();

        if (settings.IncludeCoverImage && derived.CoverBlobId is { } coverBlobId)
        {
            var coverPath = await TryWriteCoverAsync(coverBlobId, screenshotDirectory, ct);
            if (coverPath is not null)
                leading.Add(new PrepScreenshot(0, coverPath) { Kind = PrepImageKind.Cover, Featured = true });
        }

        var sheetPath = await screenshots.BuildContactSheetAsync(
            frames, Path.Combine(screenshotDirectory, "00_contact_sheet.jpg"), settings, ct,
            ScreenshotService.SheetHeader(derived.MediaInfo));
        if (sheetPath is not null)
            leading.Add(new PrepScreenshot(0, sheetPath) { Kind = PrepImageKind.ContactSheet });

        var frameImages = frames
            .Select(frame => new PrepScreenshot(frame.Seconds, frame.Path) { Kind = PrepImageKind.Frame })
            .ToList();

        var framesToFeature = Math.Max(0, settings.FullSizeImageCount - leading.Count(image => image.Featured));
        foreach (var index in PickSpreadIndexes(frameImages.Count, framesToFeature))
            frameImages[index] = frameImages[index] with { Featured = true };

        return [.. leading, .. frameImages];
    }

    /// <summary>
    /// <paramref name="count"/> indexes spread evenly through <paramref name="total"/> items, each at
    /// the centre of its share: one pick is the middle, two sit at the thirds.
    /// </summary>
    internal static IReadOnlyList<int> PickSpreadIndexes(int total, int count)
    {
        count = Math.Min(count, total);
        if (count <= 0)
            return [];

        return Enumerable.Range(1, count)
            .Select(pick => (int)Math.Round(pick * total / (double)(count + 1) - 0.5, MidpointRounding.AwayFromZero))
            .Select(index => Math.Clamp(index, 0, total - 1))
            .Distinct()
            .ToList();
    }

    /// <summary>Write the video's library cover art next to the frames, or null if it cannot be read.</summary>
    private async Task<string?> TryWriteCoverAsync(string blobId, string directory, CancellationToken ct)
    {
        try
        {
            var blob = await blobs.GetBlobAsync(blobId, ct);
            if (blob is not { } found)
                return null;

            await using var source = found.Stream;
            var extension = found.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg",
            };

            var path = Path.Combine(directory, $"00_cover{extension}");
            await using (var target = File.Create(path))
                await source.CopyToAsync(target, ct);

            return new FileInfo(path).Length > 0 ? path : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A missing cover should cost the cover, not the whole screenshot run.
            logger.LogWarning(exception, "EMP Prep could not read cover blob {BlobId}.", blobId);
            return null;
        }
    }

    /// <summary>Mark an image as featured (full size) or not.</summary>
    /// <remarks>
    /// The last featured image cannot be un-featured: the upload needs at least one full-size image
    /// for the description and for the upload form's cover field.
    /// </remarks>
    public async Task<PrepPackage> SetFeaturedAsync(
        int userId,
        Guid itemId,
        int index,
        bool featured,
        CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        var images = DeserializeScreenshots(item.ScreenshotsJson).ToList();

        if (index < 0 || index >= images.Count)
            throw new EmpPrepValidationException("No such image.");

        if (images[index].Kind == PrepImageKind.ContactSheet)
            throw new EmpPrepValidationException("The contact sheet is always the screens section; it is not a full-size image.");

        if (!featured && images[index].IsFullSize && images.Count(image => image.IsFullSize) == 1)
            throw new EmpPrepValidationException("Keep at least one full-size image — feature another one first.");

        images[index] = images[index] with { Featured = featured };
        item.ScreenshotsJson = SerializeScreenshots(images);
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await AssemblePackageAsync(item, await settingsProvider.GetAsync(ct), ct);
    }

    /// <summary>Upload an item's images to hamsterimg.net.</summary>
    public async Task<PrepPackage> UploadScreenshotsAsync(
        int userId,
        Guid itemId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        var settings = await settingsProvider.GetAsync(ct);

        var existing = DeserializeScreenshots(item.ScreenshotsJson);
        if (existing.Count == 0)
            throw new EmpPrepValidationException("Generate the screenshots before uploading them.");

        var (uploaded, errors) = await imageHost.UploadAsync(existing, settings, progress, ct);

        item.ScreenshotsJson = SerializeScreenshots(uploaded);
        item.LastError = errors.Count == 0 ? null : string.Join("; ", errors);
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await AssemblePackageAsync(item, settings, ct);
    }

    // ── Torrent ──────────────────────────────────────────────────────

    /// <summary>
    /// Build the .torrent for one item.
    /// </summary>
    /// <remarks>
    /// The payload is hashed where it already lives, so nothing is copied and the library keeps a
    /// single copy of the content. With hard-link staging enabled the file is instead linked into a
    /// release-named folder first — still one copy of the bytes, but the torrent describes a folder.
    /// </remarks>
    public async Task<PrepPackage> BuildTorrentAsync(
        int userId,
        Guid itemId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var item = await RequireItemAsync(userId, itemId, ct);
        var settings = await settingsProvider.GetAsync(ct);

        if (string.IsNullOrWhiteSpace(settings.AnnounceUrl))
            throw new EmpPrepValidationException(
                "Set the EMP announce URL (including your passkey) in EMP Prep settings before building torrents.");

        try
        {
            var derived = await metadata.DeriveAsync(item.VideoId, settings, ct);
            var mediaPath = derived.MediaInfo.FilePath;

            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                throw new EmpPrepValidationException(
                    $"The video file is not readable at {mediaPath}. Check the path is mounted and re-scan if it moved.");

            var releaseName = item.ReleaseName is { Length: > 0 } stored
                ? stored
                : derived.ReleaseName;

            var request = settings.UseHardlinkStaging
                ? BuildStagedRequest(settings, releaseName, mediaPath)
                : BuildInPlaceRequest(settings, releaseName, mediaPath);

            var result = await TorrentBuilder.BuildAsync(request, progress, ct);

            var itemDirectory = ResolveItemDirectory(settings, item, derived);
            Directory.CreateDirectory(itemDirectory);
            var torrentPath = Path.Combine(itemDirectory, $"{TorrentBuilder.SanitizeName(releaseName)}.torrent");
            await File.WriteAllBytesAsync(torrentPath, result.Contents, ct);

            item.ReleaseName = releaseName;
            item.TorrentPath = torrentPath;
            item.InfoHash = result.InfoHash;
            item.TorrentTotalBytes = result.TotalBytes;
            item.TorrentPieceLength = result.PieceLength;
            item.TorrentPieceCount = result.PieceCount;
            item.Status = PrepItemStatus.Ready;
            item.LastError = null;

            logger.LogInformation(
                "EMP Prep built {TorrentPath} ({PieceCount} pieces of {PieceLength} bytes, infohash {InfoHash}) for video {VideoId}.",
                torrentPath, result.PieceCount, result.PieceLength, result.InfoHash, item.VideoId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Status = PrepItemStatus.Error;
            item.LastError = exception.Message;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await AssemblePackageAsync(item, settings, ct);
    }

    /// <summary>A single-file torrent hashed straight from the library path.</summary>
    private TorrentRequest BuildInPlaceRequest(EmpPrepSettings settings, string releaseName, string mediaPath)
    {
        // The torrent's name must be the file's own name for an in-place single-file torrent:
        // a client checks for <savepath>/<name>, so renaming here would mean a failed recheck.
        var fileName = Path.GetFileName(mediaPath);

        return new TorrentRequest
        {
            Name = fileName,
            SingleFile = true,
            Files = [new TorrentFileEntry(mediaPath, [fileName])],
            AnnounceUrl = settings.AnnounceUrl,
            Source = settings.Source,
            Comment = Blank(settings.TorrentComment),
            PieceLength = settings.PieceLengthKiB > 0 ? settings.PieceLengthKiB * 1024 : null,
            PieceCacheDirectory = PieceCacheDirectory(settings),
        };
    }

    /// <summary>
    /// A folder-style torrent whose payload is hard-linked into a release-named directory, so the
    /// release can carry a proper name without a second copy of the bytes.
    /// </summary>
    private TorrentRequest BuildStagedRequest(EmpPrepSettings settings, string releaseName, string mediaPath)
    {
        var stagingRoot = RequireUsablePackFolder(settings, [mediaPath]);
        var folderName = TorrentBuilder.SanitizeName(releaseName);
        var releaseDirectory = Path.Combine(stagingRoot, folderName);

        // Keep the original extension: the container matters to whoever downloads it.
        var stagedName = folderName + Path.GetExtension(mediaPath);
        var stagedPath = Path.Combine(releaseDirectory, stagedName);

        HardLink.Create(mediaPath, stagedPath);

        return new TorrentRequest
        {
            Name = folderName,
            SingleFile = false,
            Files = [new TorrentFileEntry(stagedPath, [stagedName])],
            AnnounceUrl = settings.AnnounceUrl,
            Source = settings.Source,
            Comment = Blank(settings.TorrentComment),
            PieceLength = settings.PieceLengthKiB > 0 ? settings.PieceLengthKiB * 1024 : null,
            PieceCacheDirectory = PieceCacheDirectory(settings),
        };
    }

    // ── Packs ────────────────────────────────────────────────────────

    /// <summary>Load a batch's items as pack scenes, in the batch's order.</summary>
    /// <remarks>
    /// A video whose metadata cannot be read still becomes a scene — with no file — so the pack's
    /// rule check reports it instead of the whole pack failing to load.
    /// </remarks>
    private async Task<List<PackScene>> LoadScenesAsync(IReadOnlyList<PrepItem> items, EmpPrepSettings settings, CancellationToken ct)
        => ScenesFrom(items, await metadata.DeriveManyAsync(items.Select(item => item.VideoId).ToList(), settings, probeAudio: false, ct));

    /// <summary>Pair items with their derived metadata as pack scenes, in the batch's order.</summary>
    /// <remarks>
    /// A video that could not be read still becomes a scene — with no file — so the pack's rule
    /// check reports it instead of the whole pack failing to load.
    /// </remarks>
    private static List<PackScene> ScenesFrom(IReadOnlyList<PrepItem> items, IReadOnlyDictionary<int, PrepMetadata> derived)
        => items.Select(item =>
        {
            var shots = DeserializeScreenshots(item.ScreenshotsJson);
            return new PackScene
            {
                ItemId = item.Id,
                VideoId = item.VideoId,
                Metadata = derived.GetValueOrDefault(item.VideoId) ?? new PrepMetadata { VideoId = item.VideoId, Title = $"Video {item.VideoId}" },
                Sheet = shots.FirstOrDefault(shot => shot.Kind == PrepImageKind.ContactSheet),
                Cover = shots.FirstOrDefault(shot => shot.Kind == PrepImageKind.Cover) ?? shots.FirstOrDefault(shot => shot.IsFullSize),
            };
        }).ToList();

    /// <summary>The scene supplying the pack's cover: the chosen one, else the first with a cover.</summary>
    private static PackScene? CoverScene(PrepBatch batch, IReadOnlyList<PackScene> scenes)
        => scenes.FirstOrDefault(scene => scene.ItemId == batch.CoverItemId && scene.Cover is not null)
            ?? scenes.FirstOrDefault(scene => scene.Cover is not null);

    private PackPackage AssemblePack(PrepBatch batch, IReadOnlyList<PackScene> scenes, EmpPrepSettings settings)
    {
        var stats = PackComposer.Stats(scenes);
        var theme = batch.Theme ?? "";
        var generated = GeneratedPackPost(batch, scenes, stats, settings, batch.PostTitleOverride);

        var tags = string.IsNullOrWhiteSpace(batch.TagsOverride)
            ? generated.Tags
            : EmpTagFormatter.ParseUserTags(batch.TagsOverride, settings.LowercaseTags);

        var issues = PackComposer.Check(scenes, theme, stats).ToList();
        if (PackLayout.DescribeFolderProblem(settings.PackDirectory, LibraryPaths()) is { } folderProblem)
            issues.Add(new PackIssue(PackIssue.Error, folderProblem));
        else if (PackLayout.OnOtherDrives(settings.PackDirectory, scenes.Select(scene => scene.SourcePath).Where(path => path.Length > 0)) is { Count: > 0 } elsewhere)
            issues.Add(new PackIssue(PackIssue.Error, OtherDrivesMessage(settings.PackDirectory, elsewhere)));

        return new PackPackage
        {
            BatchId = batch.Id,
            PackType = PackComposer.PackType(scenes.Count),
            Theme = theme,
            PostTitle = generated.Title,
            Tags = tags,
            TagString = EmpTagFormatter.Join(tags),
            Description = string.IsNullOrWhiteSpace(batch.DescriptionOverride)
                ? generated.Description
                : LiveDescription.Fill(batch.DescriptionOverride!, PackComposer.DescriptionSlots(scenes, batch.Theme, CoverScene(batch, scenes)?.Cover, settings)),
            CoverItemId = CoverScene(batch, scenes)?.ItemId,
            CoverImageUrl = CoverScene(batch, scenes)?.Cover?.Url,
            EditedFields = new (string Name, string? Value)[]
                {
                    ("postTitle", batch.PostTitleOverride),
                    ("tags", batch.TagsOverride),
                    ("description", batch.DescriptionOverride),
                }
                .Where(field => !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => field.Name)
                .ToList(),
            Stats = stats,
            Issues = issues,
            ScreensShown = PackComposer.ScreensToShow(scenes.Count).Count,
            PackFolder = batch.PackFolder,
            TorrentPath = batch.TorrentPath,
            InfoHash = batch.InfoHash,
            TorrentTotalBytes = batch.TorrentTotalBytes,
            TorrentPieceLength = batch.TorrentPieceLength,
            TorrentPieceCount = batch.TorrentPieceCount,
        };
    }

    /// <summary>
    /// The post as generated, given an optional title edit (an edited title flows into the
    /// description's heading).
    /// </summary>
    private static (string Title, IReadOnlyList<string> Tags, string Description) GeneratedPackPost(
        PrepBatch batch,
        IReadOnlyList<PackScene> scenes,
        PackStats stats,
        EmpPrepSettings settings,
        string? titleEdit)
    {
        var title = string.IsNullOrWhiteSpace(titleEdit) ? PackComposer.Title(stats, settings) : titleEdit!;
        var tags = PackComposer.Tags(stats, settings);
        var description = PackComposer.Description(scenes, stats, title, batch.Theme ?? "", CoverScene(batch, scenes)?.Cover, settings);
        return (title, tags, description);
    }

    private async Task<(PrepBatch Batch, List<PrepItem> Items, EmpPrepSettings Settings)> RequirePackAsync(
        int userId, Guid batchId, CancellationToken ct)
    {
        var batch = await RequireBatchAsync(userId, batchId, ct);
        var items = await db.Set<PrepItem>()
            .AsTracking()
            .Where(item => item.BatchId == batchId)
            .OrderBy(item => item.Position)
            .ToListAsync(ct);

        if (items.Count < 2)
            throw new EmpPrepValidationException("A pack needs at least two videos. A single video is uploaded on its own.");

        return (batch, items, await settingsProvider.GetAsync(ct));
    }

    /// <summary>
    /// Save the theme, cover choice and post edits. As with single videos, a value equal to the
    /// generated one is stored as "no edit", so later changes to the scenes still reach the post.
    /// </summary>
    public async Task<PackPackage> UpdatePackAsync(int userId, Guid batchId, PackEdits edits, CancellationToken ct = default)
    {
        var (batch, items, settings) = await RequirePackAsync(userId, batchId, ct);

        if (edits.CoverItemId is { } coverItemId && items.All(item => item.Id != coverItemId))
            throw new EmpPrepValidationException("The cover must come from a video in this pack.");

        var scenes = await LoadScenesAsync(items, settings, ct);
        var stats = PackComposer.Stats(scenes);

        // What the description showed before this save, which a submitted edit may still contain.
        var previousTheme = batch.Theme;
        var previousCover = CoverScene(batch, scenes)?.Cover;

        batch.Theme = Blank(edits.Theme);
        batch.CoverItemId = edits.CoverItemId ?? batch.CoverItemId;

        var title = Blank(edits.PostTitle);
        batch.PostTitleOverride = title == PackComposer.Title(stats, settings) ? null : title;

        var generated = GeneratedPackPost(batch, scenes, stats, settings, batch.PostTitleOverride);

        var tags = Blank(edits.Tags);
        batch.TagsOverride = tags is not null
            && EmpTagFormatter.ParseUserTags(tags, settings.LowercaseTags).SequenceEqual(generated.Tags)
                ? null
                : tags;

        // An edited description keeps its theme, cover and screens as markers, so a theme typed at
        // the top or screens uploaded later still reach it without resetting the edit.
        var description = Blank(edits.Description) is { } submitted
            ? LiveDescription.Templatize(submitted, PackComposer.DescriptionSlots(
                scenes, batch.Theme, CoverScene(batch, scenes)?.Cover, settings, previousTheme, previousCover))
            : null;
        batch.DescriptionOverride = description is not null
            && SameText(LiveDescription.Fill(description, PackComposer.DescriptionSlots(scenes, batch.Theme, CoverScene(batch, scenes)?.Cover, settings)), generated.Description)
                ? null
                : description;

        batch.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return AssemblePack(batch, scenes, settings);
    }

    /// <summary>
    /// Upload what the pack's post shows: the cover, and the contact sheet of every scene the
    /// description displays. Scenes' other images are not uploaded for a pack.
    /// </summary>
    public async Task<PackPackage> UploadPackImagesAsync(
        int userId,
        Guid batchId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var (batch, items, settings) = await RequirePackAsync(userId, batchId, ct);
        var scenes = await LoadScenesAsync(items, settings, ct);

        var shown = PackComposer.ScreensToShow(scenes.Count).Select(index => scenes[index].ItemId).ToHashSet();
        var coverScene = CoverScene(batch, scenes);
        var errors = new List<string>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var shots = DeserializeScreenshots(item.ScreenshotsJson);
            var cover = coverScene?.ItemId == item.Id ? coverScene.Cover : null;

            bool Wanted(PrepScreenshot image)
                => (image.Kind == PrepImageKind.ContactSheet && shown.Contains(item.Id))
                    || (cover is not null && image.Path == cover.Path);

            if (!shots.Any(Wanted))
                continue;

            var (uploaded, itemErrors) = await imageHost.UploadAsync(shots, settings, null, ct, Wanted);
            item.ScreenshotsJson = SerializeScreenshots(uploaded);
            item.UpdatedAtUtc = DateTime.UtcNow;
            errors.AddRange(itemErrors);
            progress?.Report((double)(index + 1) / items.Count);
        }

        batch.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (errors.Count > 0)
            throw new EmpPrepValidationException("Some images did not upload: " + string.Join("; ", errors));

        return AssemblePack(batch, await LoadScenesAsync(items, settings, ct), settings);
    }

    /// <summary>
    /// Build the pack's folder and its torrent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder, named for the pack, is created under the pack folder setting. Each video is
    /// hard-linked into it — a second name for the same data, so nothing is copied and nothing in the
    /// library moves — and each scene's contact sheet is copied into its Screens folder, as EMP
    /// requires for packs. The torrent describes exactly that folder, so a client pointed at the pack
    /// folder setting as its save path seeds it in place.
    /// </para>
    /// <para>
    /// Rebuilding after the pack changed removes the links and screens this extension made last time
    /// that are no longer part of the pack. It only ever removes files it recorded creating, so
    /// anything else placed in the folder is left alone — and removing a link never touches the
    /// library's copy of the video.
    /// </para>
    /// </remarks>
    public async Task<PackPackage> BuildPackTorrentAsync(
        int userId,
        Guid batchId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var (batch, items, settings) = await RequirePackAsync(userId, batchId, ct);

        if (string.IsNullOrWhiteSpace(settings.AnnounceUrl))
            throw new EmpPrepValidationException(
                "Set the EMP announce URL (including your passkey) in EMP Prep settings before building torrents.");

        var scenes = await LoadScenesAsync(items, settings, ct);
        var pack = AssemblePack(batch, scenes, settings);

        var blocking = pack.Issues.Where(issue => issue.Severity == PackIssue.Error).ToList();
        if (blocking.Count > 0)
            throw new EmpPrepValidationException(string.Join(" ", blocking.Select(issue => issue.Message)));

        var missing = scenes.Where(scene => !File.Exists(scene.SourcePath)).Select(scene => scene.SourcePath).ToList();
        if (missing.Count > 0)
            throw new EmpPrepValidationException("These video files are not readable: " + string.Join(", ", missing));

        var packRoot = RequireUsablePackFolder(settings, scenes.Select(scene => scene.SourcePath));
        var folderName = TorrentBuilder.SanitizeName(pack.PostTitle);
        var folder = Path.Combine(packRoot, folderName);

        var entries = PackLayout.Plan(scenes.Select(scene => (scene.SourcePath, scene.Sheet?.Path)).ToList());
        var targets = PackLayout.Targets(folder, entries);

        RemoveStalePackFiles(settings, batch.Id, packRoot, targets);
        PackLayout.Materialize(folder, entries, ct);
        await RecordPackFilesAsync(settings, batch.Id, targets, ct);

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = folderName,
            SingleFile = false,
            Files = entries.Select((entry, index) => new TorrentFileEntry(targets[index], entry.RelativePath)).ToList(),
            AnnounceUrl = settings.AnnounceUrl,
            Source = settings.Source,
            Comment = Blank(settings.TorrentComment),
            PieceLength = settings.PieceLengthKiB > 0 ? settings.PieceLengthKiB * 1024 : null,
            PieceCacheDirectory = PieceCacheDirectory(settings),
        }, progress, ct);

        var torrentDirectory = Path.Combine(settingsProvider.ResolveOutputDirectory(settings), "packs");
        Directory.CreateDirectory(torrentDirectory);
        var torrentPath = Path.Combine(torrentDirectory, $"{folderName} [{batch.Id.ToString("N")[..8]}].torrent");
        await File.WriteAllBytesAsync(torrentPath, result.Contents, ct);

        batch.PackFolder = folder;
        batch.TorrentPath = torrentPath;
        batch.InfoHash = result.InfoHash;
        batch.TorrentTotalBytes = result.TotalBytes;
        batch.TorrentPieceLength = result.PieceLength;
        batch.TorrentPieceCount = result.PieceCount;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "EMP Prep built pack {Folder} ({Files} files, {Bytes} bytes, infohash {InfoHash}).",
            folder, entries.Count, result.TotalBytes, result.InfoHash);

        return AssemblePack(batch, scenes, settings);
    }

    /// <summary>Where piece hashes are kept so an unchanged payload is never hashed twice.</summary>
    private string PieceCacheDirectory(EmpPrepSettings settings)
        => Path.Combine(settingsProvider.ResolveOutputDirectory(settings), "piece-cache");

    /// <summary>The pack folder, checked against the rules that make hard-linking into it safe.</summary>
    private string RequireUsablePackFolder(EmpPrepSettings settings, IEnumerable<string> videoPaths)
    {
        var root = EmpPrepSettingsProvider.RequirePackDirectory(settings);

        if (PackLayout.DescribeFolderProblem(root, LibraryPaths()) is { } problem)
            throw new EmpPrepValidationException(problem);

        var elsewhere = PackLayout.OnOtherDrives(root, videoPaths);
        if (elsewhere.Count > 0)
            throw new EmpPrepValidationException(OtherDrivesMessage(root, elsewhere));

        return root;
    }

    private IEnumerable<string> LibraryPaths() => config.CovePaths.Select(path => path.Path);

    private static string OtherDrivesMessage(string packDirectory, IReadOnlyList<string> elsewhere)
        => $"{elsewhere.Count} video{(elsewhere.Count == 1 ? " is" : "s are")} on a different drive from the pack folder ({packDirectory}), "
            + $"so {(elsewhere.Count == 1 ? "it" : "they")} cannot be hard-linked into it: {string.Join(", ", elsewhere.Take(3))}"
            + (elsewhere.Count > 3 ? $" and {elsewhere.Count - 3} more" : "") + ".";

    private string PackManifestPath(EmpPrepSettings settings, Guid batchId)
        => Path.Combine(settingsProvider.ResolveOutputDirectory(settings), "packs", $"{batchId:N}.files.json");

    /// <summary>
    /// Delete files this extension created for the pack last time that are not part of it now, then
    /// any folders that left empty. Files are only removed if they were recorded as created here.
    /// </summary>
    private void RemoveStalePackFiles(EmpPrepSettings settings, Guid batchId, string packRoot, IReadOnlyList<string> keep)
    {
        var manifest = PackManifestPath(settings, batchId);
        if (!File.Exists(manifest))
            return;

        List<string> previous;
        try
        {
            previous = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifest), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return;
        }

        var keepSet = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in previous.Where(path => !keepSet.Contains(path)))
        {
            try
            {
                if (File.Exists(stale))
                    File.Delete(stale);

                // Tidy the Screens folder and the old pack folder if that left them empty — but never
                // climb to the pack root itself, which is the user's folder, not ours.
                var directory = Path.GetDirectoryName(stale);
                while (directory is not null
                    && IsStrictlyInside(directory, packRoot)
                    && Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "EMP Prep could not remove stale pack file {Path}.", stale);
            }
        }
    }

    internal static bool IsStrictlyInside(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RecordPackFilesAsync(EmpPrepSettings settings, Guid batchId, IReadOnlyList<string> files, CancellationToken ct)
    {
        var manifest = PackManifestPath(settings, batchId);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(files, JsonOptions), ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Per-item artifact directory, named for the release so it is recognisable on disk.</summary>
    private string ResolveItemDirectory(EmpPrepSettings settings, PrepItem item, PrepMetadata derived)
    {
        var name = TorrentBuilder.SanitizeName(item.ReleaseName ?? derived.ReleaseName);
        // Suffix with the video id: two videos can legitimately resolve to the same release name.
        return Path.Combine(settingsProvider.ResolveOutputDirectory(settings), $"{name} [v{item.VideoId}]");
    }

    private async Task<PrepBatch> RequireBatchAsync(int userId, Guid batchId, CancellationToken ct)
        => await db.Set<PrepBatch>().SingleOrDefaultAsync(batch => batch.Id == batchId && batch.UserId == userId, ct)
            ?? throw new EmpPrepValidationException("That prep batch does not exist.");

    /// <summary>
    /// Load an item, confirming it belongs to a batch this user owns.
    /// </summary>
    private async Task<PrepItem> RequireItemAsync(int userId, Guid itemId, CancellationToken ct, bool tracking = true)
        => await OwnedItemQuery(db.Set<PrepItem>(), db.Set<PrepBatch>(), userId, itemId, tracking).SingleOrDefaultAsync(ct)
            ?? throw new EmpPrepValidationException("That prep item does not exist.");

    /// <summary>
    /// The item with <paramref name="itemId"/>, if it sits in a batch owned by <paramref name="userId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is checked inside the same query, so an item id from another account looks exactly
    /// like one that does not exist.
    /// </para>
    /// <para>
    /// Tracking is set explicitly on the whole query, and the ownership check is an <c>Any</c>
    /// subquery rather than a join with its own tracking mode. EF Core applies
    /// <c>AsNoTracking</c> to the entire query wherever it appears. An earlier version joined against
    /// <c>batches.AsNoTracking()</c>, which silently returned untracked items, so every update —
    /// generated screenshots, edits, uploads, torrents — saved nothing and raised no error.
    /// </para>
    /// </remarks>
    internal static IQueryable<PrepItem> OwnedItemQuery(
        IQueryable<PrepItem> items,
        IQueryable<PrepBatch> batches,
        int userId,
        Guid itemId,
        bool tracking)
        => (tracking ? items.AsTracking() : items.AsNoTracking())
            .Where(item => item.Id == itemId
                && batches.Any(batch => batch.Id == item.BatchId && batch.UserId == userId));

    private static string NormalizeBatchName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return DefaultBatchName;
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Decide which submitted values are genuine edits worth storing.
    /// </summary>
    /// <remarks>
    /// The item form submits every field on save. Storing the untouched ones would freeze them: a
    /// later library change, a new tag rule or a new description template would never reach the
    /// item. So a value equal to what would be generated is stored as null ("use the generated
    /// value"), and so is a blank one. Each field is compared against what it would be given the
    /// edits before it — an edited scene title changes the generated post title and description.
    /// </remarks>
    internal static (string? Title, string? PostTitle, string? Tags, string? Description) StoredEdits(
        PrepMetadata derived,
        IReadOnlyList<PrepScreenshot> shots,
        ItemOverrides submitted,
        EmpPrepSettings settings)
    {
        var title = Blank(submitted.Title);
        title = title == derived.Title ? null : title;
        var effective = derived with { Title = title ?? derived.Title };

        var generatedPostTitle = MetadataBuilder.RenderPostTitle(effective, settings);
        var postTitle = Blank(submitted.PostTitle);
        postTitle = postTitle == generatedPostTitle ? null : postTitle;
        effective = effective with { PostTitle = postTitle ?? generatedPostTitle };

        var tags = Blank(submitted.Tags);
        tags = tags is not null
            && EmpTagFormatter.ParseUserTags(tags, settings.LowercaseTags).SequenceEqual(derived.FormattedTags)
                ? null
                : tags;
        var effectiveTags = tags is null
            ? derived.FormattedTags
            : EmpTagFormatter.ParseUserTags(tags, settings.LowercaseTags);

        var description = Blank(submitted.Description);
        description = description is not null
            && SameText(description, MetadataBuilder.RenderDescription(effective, shots, effectiveTags, settings))
                ? null
                : description;
        // Store the edit with its images as markers, so later screens and uploads still reach it.
        description = description is null ? null : LiveDescription.Templatize(description, MetadataBuilder.DescriptionSlots(shots));

        return (title, postTitle, tags, description);
    }

    /// <summary>Equal apart from line endings and surrounding whitespace, which a textarea may change.</summary>
    private static bool SameText(string left, string right)
        => left.Replace("\r\n", "\n").Trim() == right.Replace("\r\n", "\n").Trim();

    private static string SerializeScreenshots(IReadOnlyList<PrepScreenshot> shots)
        => JsonSerializer.Serialize(shots, JsonOptions);

    internal static IReadOnlyList<PrepScreenshot> DeserializeScreenshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<PrepScreenshot>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
