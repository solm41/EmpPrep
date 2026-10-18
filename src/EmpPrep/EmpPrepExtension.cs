using System.Text.Json;
using System.Text.Json.Serialization;

using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;

using EmpPrep.Data;
using EmpPrep.Models;
using EmpPrep.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmpPrep;

/// <summary>
/// EMP Prep — turns a Cove selection into an upload-ready package.
/// </summary>
/// <remarks>
/// <para>
/// The workflow: select videos in the library and send them to a prep batch (or send one from a
/// video's own menu), then per item generate full-resolution screenshots, optionally host them,
/// and build a private .torrent. The page then exposes each field — release name, dotted tags,
/// performers, studio, BBCode description — ready to copy into the upload form.
/// </para>
/// <para>
/// The design constraint that shapes everything: the payload is never copied. The torrent is
/// hashed from the file at its existing library path, so seeding is a matter of pointing the
/// client at the folder that is already there. Only screenshots and the .torrent itself are
/// written, into the extension's own output directory.
/// </para>
/// </remarks>
public sealed class EmpPrepExtension : FullExtensionBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly EmpPrepSettingsProvider _settings = new();

    /// <summary>
    /// The extension's data directory, used as the default artifact root.
    /// </summary>
    /// <remarks>
    /// <see cref="ExtensionContext"/> is only handed to <see cref="ConfigureServices"/>, so it is
    /// captured there and consumed once the store is available in <see cref="InitializeAsync"/>.
    /// </remarks>
    private string _dataDirectory = "";

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        _dataDirectory = context.DataDirectory;

        // The provider is constructed here but only usable once the host hands over the key-value
        // store, which happens before InitializeAsync.
        services.AddSingleton(_settings);
        services.AddSingleton<PrepJobTracker>();

        // Singletons: stateless apart from cached binary paths.
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<SpriteReader>();
        services.AddSingleton<MediaProbeCache>();
        services.AddSingleton<ScreenshotService>();
        services.AddSingleton<HamsterImgUploader>();
        services.AddHttpClient();

        // Scoped: these take CoveContext, so they share the request's (or job scope's) unit of work.
        services.AddScoped<MetadataBuilder>();
        services.AddScoped<EmpPrepService>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // The host applies DefineMigrations before this runs, so the tables already exist.
        _settings.Attach(Store, _dataDirectory);
        return Task.CompletedTask;
    }

    public override void ConfigureModel(ModelBuilder modelBuilder) => EmpPrepSchema.Configure(modelBuilder);

    protected override void DefineMigrations()
    {
        Migration("001_empprep", EmpPrepSchema.Sql);
        Migration("002_empprep_piece_info", EmpPrepSchema.PieceInfoSql);
        Migration("003_empprep_post_title", EmpPrepSchema.PostTitleSql);
        Migration("004_empprep_packs", EmpPrepSchema.PackSql);
    }

    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddPage("emp-prep", "EMP Prep", "EmpPrepPage", icon: "upload-cloud", showInNav: true, navOrder: 95)
            // Multi-select toolbar in the video list.
            .AddAction(
                "emp-prep-add-selection",
                "Add to EMP prep",
                actionType: "bulk",
                entityTypes: ["video"],
                icon: "upload-cloud",
                apiEndpoint: "/api/ext/empprep/add",
                order: 50)
            // Single video, from the detail page's operations menu.
            .AddAction(
                "emp-prep-add-video",
                "Add to EMP prep",
                actionType: "toolbar",
                entityTypes: ["video"],
                icon: "upload-cloud",
                apiEndpoint: "/api/ext/empprep/add",
                order: 50)
            .AddSettingsTab("emp-prep", "EMP Prep", order: 60, icon: "upload-cloud",
                description: "Announce URL, screenshot rules, output paths, and image hosting for tracker uploads.")
            .AddSettingsSection("emp-prep", "EMP Prep", "EmpPrepSettingsPanel", order: 10)
            .Build();

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Reading the library's metadata is the core of every route here, so they all require
        // videos.read. Nothing in this extension writes to the library itself.
        var group = endpoints
            .MapGroup("/api/ext/empprep")
            .WithTags("EMP Prep")
            .RequireCovePermission(Permissions.VideosRead);

        MapBatchEndpoints(group);
        MapItemEndpoints(group);
        MapArtifactEndpoints(group);
        MapSettingsEndpoints(group);
    }

    // ── Batches & queueing ───────────────────────────────────────────

    private static void MapBatchEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/batches", (EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.ListBatchesAsync(UserId(principal), ct)));

        group.MapPost("/batches", (CreateBatchRequest body, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.CreateBatchAsync(UserId(principal), body.Name, ct)));

        group.MapGet("/batches/{batchId:guid}", (Guid batchId, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.GetBatchAsync(UserId(principal), batchId, ct)));

        group.MapPut("/batches/{batchId:guid}", (Guid batchId, CreateBatchRequest body, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () =>
            {
                await service.RenameBatchAsync(UserId(principal), batchId, body.Name ?? "", ct);
                return await service.GetBatchAsync(UserId(principal), batchId, ct);
            }));

        group.MapDelete("/batches/{batchId:guid}", (Guid batchId, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () =>
            {
                await service.DeleteBatchAsync(UserId(principal), batchId, ct);
                return new { deleted = true };
            }));

        // The endpoint both library actions post to. The host's bulk and single-entity action
        // payloads both carry entityIds, so one handler serves the multi-select toolbar and the
        // video detail menu.
        group.MapPost("/add", (AddSelectionRequest body, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () =>
            {
                var ids = body.EntityIds ?? body.SelectedIds ?? [];
                var (batchId, batchName, added, skipped) = await service.AddVideosAsync(
                    UserId(principal), body.BatchId, body.NewBatchName, ids, ct);

                var message = skipped > 0
                    ? $"Added {added} video{(added == 1 ? "" : "s")} to EMP prep batch \"{batchName}\" ({skipped} already queued)."
                    : $"Added {added} video{(added == 1 ? "" : "s")} to EMP prep batch \"{batchName}\".";

                return new { batchId, batchName, added, skipped, message };
            }));
    }

    // ── Per-item operations ──────────────────────────────────────────

    private static void MapItemEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/items/{itemId:guid}", (Guid itemId, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.GetItemAsync(UserId(principal), itemId, ct)));

        group.MapPut("/items/{itemId:guid}", (Guid itemId, ItemOverrides body, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.UpdateItemAsync(UserId(principal), itemId, body, ct)));

        group.MapDelete("/items/{itemId:guid}", (Guid itemId, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () =>
            {
                await service.RemoveItemAsync(UserId(principal), itemId, ct);
                return new { removed = true };
            }));

        // Long-running steps run as host jobs: extracting forty frames or hashing a 10 GB file
        // takes far longer than a request should hold open, and the work must survive the caller
        // navigating away.
        group.MapPost("/items/{itemId:guid}/screenshots", (
            Guid itemId,
            GenerateScreenshotsRequest? body,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            var timestamps = body?.Timestamps;

            return StartJob(jobs, scopeFactory, tracker, logger, itemId, "screenshots",
                "EMP Prep: extract screenshots",
                (service, progress, ct) => service.GenerateScreenshotsAsync(userId, itemId, timestamps, progress, ct));
        });

        group.MapPost("/items/{itemId:guid}/screenshots/upload", (
            Guid itemId,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            return StartJob(jobs, scopeFactory, tracker, logger, itemId, "upload",
                "EMP Prep: upload images to hamsterimg",
                (service, progress, ct) => service.UploadScreenshotsAsync(userId, itemId, progress, ct));
        });

        group.MapPost("/items/{itemId:guid}/torrent", (
            Guid itemId,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            return StartJob(jobs, scopeFactory, tracker, logger, itemId, "torrent",
                "EMP Prep: build torrent",
                (service, progress, ct) => service.BuildTorrentAsync(userId, itemId, progress, ct));
        });

        group.MapPut("/items/{itemId:guid}/screenshots/{index:int}/featured", (
            Guid itemId,
            int index,
            SetFeaturedRequest body,
            EmpPrepService service,
            ICurrentPrincipalAccessor principal,
            CancellationToken ct) =>
            ResultAsync(async () => await service.SetFeaturedAsync(UserId(principal), itemId, index, body.Featured, ct)));

        // ── Packs: one torrent and one post for a multi-video batch ──
        group.MapPut("/batches/{batchId:guid}/pack", (Guid batchId, PackEdits body, EmpPrepService service, ICurrentPrincipalAccessor principal, CancellationToken ct) =>
            ResultAsync(async () => await service.UpdatePackAsync(UserId(principal), batchId, body, ct)));

        // Pack jobs are tracked under the batch id, so the page shows their progress on the pack.
        // Screens for a whole batch in one job — never a job per video.
        group.MapPost("/batches/{batchId:guid}/screens", (
            Guid batchId,
            BatchScreensRequest? body,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            var missingOnly = body?.MissingOnly ?? true;
            return StartJob(jobs, scopeFactory, tracker, logger, batchId, "screenshots",
                missingOnly ? "EMP Prep: generate missing screens for a pack" : "EMP Prep: regenerate all screens for a pack",
                (service, progress, ct) => service.GenerateBatchScreensAsync(userId, batchId, missingOnly, progress, ct));
        });

        group.MapPost("/batches/{batchId:guid}/pack/upload", (
            Guid batchId,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            return StartJob(jobs, scopeFactory, tracker, logger, batchId, "upload",
                "EMP Prep: upload pack images to hamsterimg",
                (service, progress, ct) => service.UploadPackImagesAsync(userId, batchId, progress, ct));
        });

        group.MapPost("/batches/{batchId:guid}/pack/torrent", (
            Guid batchId,
            IJobService jobs,
            IExtensionServiceScopeFactory scopeFactory,
            PrepJobTracker tracker,
            ICurrentPrincipalAccessor principal,
            ILogger<EmpPrepExtension> logger) =>
        {
            var userId = UserId(principal);
            return StartJob(jobs, scopeFactory, tracker, logger, batchId, "torrent",
                "EMP Prep: build pack torrent",
                (service, progress, ct) => service.BuildPackTorrentAsync(userId, batchId, progress, ct));
        });

        group.MapGet("/jobs", (PrepJobTracker tracker) => Results.Json(tracker.Active(), JsonOpts));

        group.MapGet("/videos/{videoId:int}/sprite", (int videoId, EmpPrepService service, CancellationToken ct) =>
            ResultAsync(async () => await service.GetSpriteAsync(videoId, ct)));

        group.MapGet("/videos/{videoId:int}/plan", (int videoId, EmpPrepService service, CancellationToken ct) =>
            ResultAsync(async () => new { timestamps = await service.PlanTimestampsAsync(videoId, ct) }));
    }

    /// <summary>
    /// Enqueue one prep step on the host job queue and return its job id immediately.
    /// </summary>
    /// <remarks>
    /// The callback resolves its own scope from <see cref="IExtensionServiceScopeFactory"/> rather
    /// than capturing the request's services: the job outlives the request, and an extension-owned
    /// scope is what keeps the provider generation alive for the duration of the work.
    ///
    /// Jobs are non-exclusive so preparing several items does not serialise behind Cove's scan and
    /// generate queue — but each is bounded by the ffmpeg concurrency limit inside the services.
    /// </remarks>
    private static IResult StartJob(
        IJobService jobs,
        IExtensionServiceScopeFactory scopeFactory,
        PrepJobTracker tracker,
        ILogger logger,
        Guid itemId,
        string kind,
        string description,
        Func<EmpPrepService, IProgress<double>, CancellationToken, Task> work)
    {
        var jobId = jobs.Enqueue($"empprep-{kind}", description, async (progress, ct) =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<EmpPrepService>();

                var reporter = new Progress<double>(fraction =>
                {
                    tracker.Report(itemId, fraction);
                    progress.Report(fraction * 100);
                });

                await work(service, reporter, ct);
                tracker.Complete(itemId);
            }
            catch (OperationCanceledException)
            {
                tracker.Complete(itemId, "Cancelled.");
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "EMP Prep {Kind} failed for item {ItemId}.", kind, itemId);
                tracker.Complete(itemId, exception.Message);
                throw;
            }
        }, exclusive: false);

        tracker.Start(itemId, jobId, kind);
        return Results.Json(new { jobId, itemId, kind, description }, JsonOpts);
    }

    // ── Artifact downloads ───────────────────────────────────────────

    private static void MapArtifactEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/items/{itemId:guid}/torrent/file", async (
            Guid itemId,
            EmpPrepService service,
            EmpPrepSettingsProvider settings,
            ICurrentPrincipalAccessor principal,
            CancellationToken ct) =>
        {
            var package = await service.GetItemAsync(UserId(principal), itemId, ct);
            if (string.IsNullOrWhiteSpace(package.TorrentPath))
                return Results.NotFound(new { message = "No torrent has been built for this item yet." });

            var root = await ResolveArtifactRootAsync(settings, ct);
            if (!TryOpenArtifact(package.TorrentPath, root, out var stream, out var error))
                return Results.NotFound(new { message = error });

            return Results.File(
                stream!,
                "application/x-bittorrent",
                Path.GetFileName(package.TorrentPath));
        });

        group.MapGet("/batches/{batchId:guid}/pack/torrent/file", async (
            Guid batchId,
            EmpPrepService service,
            EmpPrepSettingsProvider settings,
            ICurrentPrincipalAccessor principal,
            CancellationToken ct) =>
        {
            var batch = await service.GetBatchAsync(UserId(principal), batchId, ct);
            if (string.IsNullOrWhiteSpace(batch.Pack?.TorrentPath))
                return Results.NotFound(new { message = "No torrent has been built for this pack yet." });

            var root = await ResolveArtifactRootAsync(settings, ct);
            if (!TryOpenArtifact(batch.Pack.TorrentPath, root, out var stream, out var error))
                return Results.NotFound(new { message = error });

            return Results.File(stream!, "application/x-bittorrent", Path.GetFileName(batch.Pack.TorrentPath));
        });

        // Serves a generated frame back to the page for the review thumbnails.
        group.MapGet("/items/{itemId:guid}/screenshots/{index:int}/file", async (
            Guid itemId,
            int index,
            EmpPrepService service,
            EmpPrepSettingsProvider settings,
            ICurrentPrincipalAccessor principal,
            CancellationToken ct) =>
        {
            var package = await service.GetItemAsync(UserId(principal), itemId, ct);
            if (index < 0 || index >= package.Screenshots.Count)
                return Results.NotFound(new { message = "No such screenshot." });

            var path = package.Screenshots[index].Path;
            var root = await ResolveArtifactRootAsync(settings, ct);
            if (!TryOpenArtifact(path, root, out var stream, out var error))
                return Results.NotFound(new { message = error });

            var contentType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";

            return Results.File(stream!, contentType);
        });

        // The scrubbing sheet, for picking timestamps before extraction.
        group.MapGet("/videos/{videoId:int}/sprite/image", async (
            int videoId,
            SpriteReader sprites,
            CancellationToken ct) =>
        {
            var info = await sprites.ReadAsync(videoId, ct);
            if (!info.Available || info.SpritePath is null || !File.Exists(info.SpritePath))
                return Results.NotFound(new { message = "No sprite sheet has been generated for this video." });

            return Results.File(
                File.OpenRead(info.SpritePath),
                "image/jpeg");
        });
    }

    /// <summary>
    /// Open an artifact for download, refusing anything outside the configured output tree.
    /// </summary>
    /// <remarks>
    /// The paths come from this extension's own rows, not from the request, so this is defence in
    /// depth rather than the primary control: it means a hand-edited or migrated database row can
    /// never turn these two routes into a way to read arbitrary files off the server.
    /// </remarks>
    private static async Task<string> ResolveArtifactRootAsync(EmpPrepSettingsProvider settings, CancellationToken ct)
        => Path.GetFullPath(settings.ResolveOutputDirectory(await settings.GetAsync(ct)));

    private static bool TryOpenArtifact(
        string path,
        string root,
        out Stream? stream,
        out string? error)
    {
        stream = null;

        var full = Path.GetFullPath(path);

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = "That artifact is outside the configured EMP Prep output directory.";
            return false;
        }

        if (!File.Exists(full))
        {
            error = "That artifact is no longer on disk. Re-run the step that produces it.";
            return false;
        }

        stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        error = null;
        return true;
    }

    // ── Settings ─────────────────────────────────────────────────────

    private static void MapSettingsEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (EmpPrepSettingsProvider settings, FfmpegLocator ffmpeg, CoveConfiguration config, CancellationToken ct) =>
        {
            var current = await settings.GetAsync(ct);
            return Results.Json(new
            {
                settings = current,
                resolvedOutputDirectory = settings.ResolveOutputDirectory(current),
                packDirectory = string.IsNullOrWhiteSpace(current.PackDirectory) ? null : Path.GetFullPath(current.PackDirectory),
                packDirectoryProblem = PackLayout.DescribeFolderProblem(current.PackDirectory, config.CovePaths.Select(path => path.Path)),
                ffmpegPath = ffmpeg.FfmpegPath,
                ffprobePath = ffmpeg.FfprobePath,
                imageHostReady = HamsterImgUploader.IsConfigured(current),
                imageHostProblem = HamsterImgUploader.DescribeUnavailable(current),
                announceConfigured = !string.IsNullOrWhiteSpace(current.AnnounceUrl),
            }, JsonOpts);
        });

        group.MapPut("/settings", async (EmpPrepSettings body, EmpPrepSettingsProvider settings, CancellationToken ct) =>
            Results.Json(await settings.SaveAsync(body, ct), JsonOpts));
    }

    // ── Plumbing ─────────────────────────────────────────────────────

    private static int UserId(ICurrentPrincipalAccessor principal)
        => principal.Current?.UserId ?? throw new UnauthorizedException();

    /// <summary>
    /// Run a handler, mapping the extension's own validation failures onto 400 responses.
    /// </summary>
    /// <remarks>
    /// Most failures here are the user's to fix — no announce URL, no image host, a file that has
    /// moved — so they belong in the response body as a readable message rather than as a 500 with
    /// a stack trace in the log.
    /// </remarks>
    private static async Task<IResult> ResultAsync<T>(Func<Task<T>> handler)
    {
        try
        {
            return Results.Json(await handler(), JsonOpts);
        }
        catch (EmpPrepValidationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        catch (IOException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    // ── Request DTOs ─────────────────────────────────────────────────

    /// <summary>The host's action payload. Only the entity ids and our own extras are read.</summary>
    private sealed record AddSelectionRequest
    {
        public List<int>? EntityIds { get; init; }
        public List<int>? SelectedIds { get; init; }
        /// <summary>Target an existing batch (sent by the prep page, not by the library action).</summary>
        public Guid? BatchId { get; init; }
        /// <summary>Create and target a new batch by name.</summary>
        public string? NewBatchName { get; init; }
    }

    private sealed record CreateBatchRequest(string? Name);

    private sealed record GenerateScreenshotsRequest(List<double>? Timestamps);

    private sealed record SetFeaturedRequest(bool Featured);

    private sealed record BatchScreensRequest(bool MissingOnly = true);
}
