using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Cove.Core.Interfaces;

namespace EmpPrep.Services;

/// <summary>One tile in Cove's scrubbing sprite sheet.</summary>
public sealed record SpriteTile(double StartSeconds, double EndSeconds, int X, int Y, int Width, int Height);

public sealed record SpriteSheetInfo(
    bool Available,
    string? SpritePath,
    int TileWidth,
    int TileHeight,
    IReadOnlyList<SpriteTile> Tiles);

/// <summary>
/// Reads the sprite sheet Cove already generated for a video, so the UI can offer a contact sheet
/// to pick screenshot timestamps from without decoding anything.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is used for <em>picking</em> only. Cove composes sprites from 160px frames for the
/// scrubbing preview, so a tile is roughly a thumbnail — cropping one out and calling it an upload
/// screenshot would produce a visibly terrible set. Chosen timestamps are handed to
/// <see cref="ScreenshotService"/>, which re-extracts at the source resolution.
/// </para>
/// <para>
/// Paths are recomputed rather than read from the host: Cove's thumbnail service is internal to the
/// host assembly and not part of the extension ABI. The layout it uses is
/// <c>{GeneratedPath}/vtt/{first two hex chars of sha256(videoId)}/{videoId}_sprite.jpg</c>. If a
/// future Cove release changes that, this degrades to "no sprite available" and the auto-selected
/// timestamps are used instead — it never blocks a prep run.
/// </para>
/// </remarks>
public sealed class SpriteReader(CoveConfiguration config)
{
    private static readonly Regex CueTimingPattern = new(
        @"^(?<start>\d{2}:\d{2}:\d{2}[.,]\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2}[.,]\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex TilePattern = new(
        @"#xywh=(?<x>\d+),(?<y>\d+),(?<w>\d+),(?<h>\d+)",
        RegexOptions.Compiled);

    private string VttDirectory => Path.Combine(config.GeneratedPath, "vtt");

    public string GetSpritePath(int videoId)
        => Path.Combine(VttDirectory, ShardFor(videoId), $"{videoId}_sprite.jpg");

    public string GetSpriteVttPath(int videoId)
        => Path.Combine(VttDirectory, ShardFor(videoId), $"{videoId}_thumbs.vtt");

    private static string ShardFor(int videoId)
        => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)))[..2];

    public bool HasSprite(int videoId)
        => File.Exists(GetSpritePath(videoId)) && File.Exists(GetSpriteVttPath(videoId));

    /// <summary>Read the sheet's tile layout, or an unavailable result when there is no sheet.</summary>
    public async Task<SpriteSheetInfo> ReadAsync(int videoId, CancellationToken ct = default)
    {
        var spritePath = GetSpritePath(videoId);
        var vttPath = GetSpriteVttPath(videoId);

        if (!File.Exists(spritePath) || !File.Exists(vttPath))
            return new SpriteSheetInfo(false, null, 0, 0, []);

        try
        {
            var tiles = ParseVtt(await File.ReadAllTextAsync(vttPath, ct));
            if (tiles.Count == 0)
                return new SpriteSheetInfo(false, null, 0, 0, []);

            return new SpriteSheetInfo(
                true,
                spritePath,
                tiles[0].Width,
                tiles[0].Height,
                tiles);
        }
        catch (IOException)
        {
            // The sheet may be mid-regeneration. Not having it is never fatal here.
            return new SpriteSheetInfo(false, null, 0, 0, []);
        }
    }

    internal static List<SpriteTile> ParseVtt(string vtt)
    {
        var tiles = new List<SpriteTile>();
        var lines = vtt.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var timing = CueTimingPattern.Match(lines[index].Trim());
            if (!timing.Success)
                continue;

            // The payload line carrying "#xywh=" follows the timing line.
            for (var payloadIndex = index + 1; payloadIndex < lines.Length; payloadIndex++)
            {
                var payload = lines[payloadIndex].Trim();
                if (payload.Length == 0)
                    break;

                var tile = TilePattern.Match(payload);
                if (!tile.Success)
                    continue;

                tiles.Add(new SpriteTile(
                    ParseVttTime(timing.Groups["start"].Value),
                    ParseVttTime(timing.Groups["end"].Value),
                    int.Parse(tile.Groups["x"].Value, CultureInfo.InvariantCulture),
                    int.Parse(tile.Groups["y"].Value, CultureInfo.InvariantCulture),
                    int.Parse(tile.Groups["w"].Value, CultureInfo.InvariantCulture),
                    int.Parse(tile.Groups["h"].Value, CultureInfo.InvariantCulture)));
                index = payloadIndex;
                break;
            }
        }

        return tiles;
    }

    private static double ParseVttTime(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600
            + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
            + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }
}
