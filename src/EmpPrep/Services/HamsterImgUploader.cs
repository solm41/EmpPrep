using System.Net.Http.Headers;
using System.Text.Json;

using EmpPrep.Models;

using Microsoft.Extensions.Logging;

namespace EmpPrep.Services;

/// <summary>The links hamsterimg returns for one uploaded image.</summary>
public sealed record HostedImage(string Url, string? ThumbUrl, string? MediumUrl, string? ViewerUrl);

/// <summary>
/// Uploads images to hamsterimg.net, the host EMP uploads are expected to use.
/// </summary>
/// <remarks>
/// <para>
/// hamsterimg exposes the Chevereto v1 upload API: <c>POST /api/1/upload</c> with the key in an
/// <c>X-API-Key</c> header and the file in the multipart <c>source</c> field. Every upload is sent
/// with <c>nsfw=1</c>; the content is adult, and mis-flagging it breaks the host's own rules.
/// </para>
/// <para>
/// Only what the description uses is uploaded: the full-size images and the contact sheet. The
/// response's full-size, thumbnail and viewer links are all kept.
/// </para>
/// <para>
/// Uploading publishes the images to a third party, so this only ever runs from the explicit
/// "Upload images" action, never as a side effect of generating them. The key is sent to the host
/// only and never logged.
/// </para>
/// </remarks>
public sealed class HamsterImgUploader(IHttpClientFactory httpClientFactory, ILogger<HamsterImgUploader> logger)
{
    public const string UploadEndpoint = "https://hamsterimg.net/api/1/upload";

    public static bool IsConfigured(EmpPrepSettings settings) => !string.IsNullOrWhiteSpace(settings.HamsterImgApiKey);

    /// <summary>Describe why uploading is unavailable, or null when it is ready to use.</summary>
    public static string? DescribeUnavailable(EmpPrepSettings settings)
        => IsConfigured(settings)
            ? null
            : "Set your hamsterimg.net API key in EMP Prep settings (find it on hamsterimg.net under Settings → API).";

    /// <summary>
    /// Upload the full-size images and the contact sheet that are not hosted yet, returning the set
    /// with links filled in. Individual frames that only feed the contact sheet are never uploaded.
    /// </summary>
    /// <remarks>
    /// Uploads run one at a time: image hosts rate-limit, and a partial set with a clear error beats
    /// a burst that gets the key throttled. Images already carrying a link are skipped, so a retry
    /// after a partial failure uploads only what is missing.
    /// </remarks>
    public Task<(IReadOnlyList<PrepScreenshot> Images, IReadOnlyList<string> Errors)> UploadAsync(
        IReadOnlyList<PrepScreenshot> images,
        EmpPrepSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => UploadAsync(images, settings, progress, ct, include: null);

    /// <summary>Upload only the images <paramref name="include"/> selects (default: the upload set).</summary>
    public async Task<(IReadOnlyList<PrepScreenshot> Images, IReadOnlyList<string> Errors)> UploadAsync(
        IReadOnlyList<PrepScreenshot> images,
        EmpPrepSettings settings,
        IProgress<double>? progress,
        CancellationToken ct,
        Func<PrepScreenshot, bool>? include)
    {
        if (DescribeUnavailable(settings) is { } unavailable)
            throw new InvalidOperationException(unavailable);

        var results = new List<PrepScreenshot>(images.Count);
        var errors = new List<string>();

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            ct.ThrowIfCancellationRequested();

            // Already hosted, or a frame that only feeds the contact sheet: leave it alone.
            if (!string.IsNullOrWhiteSpace(image.Url) || !(include?.Invoke(image) ?? image.Uploads))
            {
                results.Add(image);
            }
            else
            {
                try
                {
                    var hosted = await UploadOneAsync(image.Path, settings, ct);
                    results.Add(image with
                    {
                        Url = hosted.Url,
                        // Fall back to the medium size, then the full image, so a frame always has
                        // something to show as its thumbnail.
                        ThumbUrl = hosted.ThumbUrl ?? hosted.MediumUrl ?? hosted.Url,
                        ViewerUrl = hosted.ViewerUrl,
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "EMP Prep failed to upload {FileName} to hamsterimg.", image.FileName);
                    errors.Add($"{image.FileName}: {exception.Message}");
                    results.Add(image);
                }
            }

            progress?.Report((double)(index + 1) / images.Count);
        }

        return (results, errors);
    }

    private async Task<HostedImage> UploadOneAsync(string path, EmpPrepSettings settings, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Image is missing from disk: {path}", path);

        var client = httpClientFactory.CreateClient("empprep-hamsterimg");
        client.Timeout = TimeSpan.FromMinutes(3);

        using var content = new MultipartFormDataContent();

        await using var fileStream = File.OpenRead(path);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(path));
        content.Add(fileContent, "source", Path.GetFileName(path));

        content.Add(new StringContent("1"), "nsfw");
        content.Add(new StringContent("json"), "format");
        if (!string.IsNullOrWhiteSpace(settings.HamsterImgAlbumId))
            content.Add(new StringContent(settings.HamsterImgAlbumId), "album_id");

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("X-API-Key", settings.HamsterImgApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return ParseResponse((int)response.StatusCode, body);
    }

    /// <summary>
    /// Read the links out of a hamsterimg (Chevereto v1) upload response, or throw with the host's
    /// own error message.
    /// </summary>
    internal static HostedImage ParseResponse(int httpStatus, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"hamsterimg returned HTTP {httpStatus} with a non-JSON body: {Truncate(body)}");
        }

        using (document)
        {
            var root = document.RootElement;

            // Chevereto reports failures in the body as well as the status, e.g.
            // {"status_code":400,"error":{"message":"Invalid API v1 key.","code":100}}
            var statusCode = root.TryGetProperty("status_code", out var status) && status.ValueKind == JsonValueKind.Number
                ? status.GetInt32()
                : httpStatus;

            if (statusCode is < 200 or >= 300 || !root.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object)
            {
                var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : null;
                throw new InvalidOperationException(
                    $"hamsterimg rejected the upload ({statusCode}): {message ?? Truncate(body)}");
            }

            var url = ReadString(image, "url")
                ?? throw new InvalidOperationException($"hamsterimg's response had no image URL: {Truncate(body)}");

            return new HostedImage(
                url,
                ReadNestedUrl(image, "thumb"),
                ReadNestedUrl(image, "medium"),
                ReadString(image, "url_viewer"));
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    private static string? ReadNestedUrl(JsonElement image, string property)
        => image.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, "url")
            : null;

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private static string Truncate(string value)
        => value.Length <= 300 ? value : value[..300] + "…";
}
