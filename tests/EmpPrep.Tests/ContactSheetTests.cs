using Cove.Core.Interfaces;

using EmpPrep.Models;
using EmpPrep.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace EmpPrep.Tests;

public sealed class ContactSheetTimestampTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(82.9, "1:22")]       // floored, not rounded: the frame is from within second 82
    [InlineData(599, "9:59")]
    [InlineData(3725, "1:02:05")]
    public void FormatTimestamp_MatchesWhatThePageShows(double seconds, string expected)
        => Assert.Equal(expected, ScreenshotService.FormatTimestamp(seconds));

    [Fact]
    public void EscapeFilterValue_EscapesTheColonsThatWouldSplitTheFilter()
    {
        // Unescaped, "1:22" ends drawtext's text option at the colon, and "C:/..." ends the font path.
        Assert.Equal(@"1\:22", ScreenshotService.EscapeFilterValue("1:22"));
        Assert.Equal(@"C\:/Windows/Fonts/arialbd.ttf", ScreenshotService.EscapeFilterValue("C:/Windows/Fonts/arialbd.ttf"));
    }

    [Fact]
    public void TimestampFilter_UsesForwardSlashesAndEscapesTheFontPath()
    {
        var filter = ScreenshotService.TimestampFilter(82, @"C:\Windows\Fonts\arialbd.ttf");

        Assert.StartsWith(@"drawtext=fontfile='C\:/Windows/Fonts/arialbd.ttf':text='1\:22'", filter);
        // Bottom-right corner, on a translucent box so it reads on any frame.
        Assert.Contains("x=w-tw-10:y=h-th-10", filter);
        Assert.Contains("box=1", filter);
    }

    [Fact]
    public void TimestampFilter_LeavesFontLookupToFfmpegWhenNoFileIsKnown()
        => Assert.StartsWith(@"drawtext=text='1\:22'", ScreenshotService.TimestampFilter(82, null));

    [Fact]
    public void Timestamps_AreOnByDefault()
        => Assert.True(new EmpPrepSettings().Normalized().ContactSheetTimestamps);
}

/// <summary>
/// Builds real contact sheets with the local ffmpeg. Skipped (passes trivially) where ffmpeg is not
/// on PATH, so the suite still runs on machines without it.
/// </summary>
public sealed class ContactSheetFfmpegTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"empprep-sheet-{Guid.NewGuid():N}");

    public ContactSheetFfmpegTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StampedSheet_DiffersFromUnstampedOnlyWhereTheTimestampsAre()
    {
        var config = new CoveConfiguration();
        var locator = new FfmpegLocator(config);
        if (locator.FfmpegPath is null)
            return;

        // Six flat grey frames: any change in the stamped sheet can only be the drawn text.
        var frames = new List<ScreenshotFrame>();
        for (var index = 0; index < 6; index++)
        {
            var path = Path.Combine(_directory, $"f{index}.jpg");
            await Run(locator.FfmpegPath, "-f", "lavfi", "-i", "color=c=gray:s=1280x720", "-frames:v", "1", "-y", path);
            frames.Add(new ScreenshotFrame(60 * index + 22, path));
        }

        var service = new ScreenshotService(locator, config, NullLogger<ScreenshotService>.Instance);
        var baseSettings = new EmpPrepSettings { ContactSheetColumns = 3, ContactSheetTileWidth = 320 }.Normalized();

        var stamped = await service.BuildContactSheetAsync(
            frames, Path.Combine(_directory, "stamped.jpg"), baseSettings with { ContactSheetTimestamps = true });
        var plain = await service.BuildContactSheetAsync(
            frames, Path.Combine(_directory, "plain.jpg"), baseSettings with { ContactSheetTimestamps = false });

        Assert.NotNull(stamped);
        Assert.NotNull(plain);

        // Same layout either way...
        Assert.Equal(await Dimensions(locator, plain!), await Dimensions(locator, stamped!));
        // ...but the stamped one has text drawn on it.
        Assert.NotEqual(await File.ReadAllBytesAsync(plain!), await File.ReadAllBytesAsync(stamped!));
    }

    private static async Task<string> Dimensions(FfmpegLocator locator, string path)
    {
        var (_, stdout, _) = await FfmpegLocator.RunAsync(
            locator.FfprobePath ?? "ffprobe",
            ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", path],
            CancellationToken.None);
        return stdout.Trim();
    }

    private static async Task Run(string ffmpeg, params string[] arguments)
    {
        var (exitCode, _, stdErr) = await FfmpegLocator.RunAsync(
            ffmpeg, ["-hide_banner", "-loglevel", "error", .. arguments], CancellationToken.None);
        Assert.True(exitCode == 0, stdErr);
    }
}
