using System.Text;

using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

public sealed class PieceCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"empprep-cache-{Guid.NewGuid():N}");

    public PieceCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TorrentRequest Request(string path, string name = "clip.mp4") => new()
    {
        Name = name,
        SingleFile = true,
        Files = [new TorrentFileEntry(path, [name])],
        AnnounceUrl = "https://tracker.example/announce",
        PieceLength = 16 * 1024,
        PieceCacheDirectory = Path.Combine(_root, "cache"),
    };

    private string WriteFile(byte seed, int length = 200_000)
    {
        var path = Path.Combine(_root, "clip.mp4");
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)(index * seed);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Rebuild_OfUnchangedFiles_ReusesTheHashes()
    {
        var path = WriteFile(3);
        var first = await TorrentBuilder.BuildAsync(Request(path));

        // A rebuild under a new name (e.g. an edited pack title) needs the same piece hashes.
        var renamed = await TorrentBuilder.BuildAsync(Request(path, "renamed.mp4"));

        Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache")));
        Assert.Equal(first.PieceCount, renamed.PieceCount);
        Assert.Equal(PiecesOf(first), PiecesOf(renamed));
    }

    [Fact]
    public async Task ChangedFile_IsHashedAgain()
    {
        var path = WriteFile(3);
        var before = await TorrentBuilder.BuildAsync(Request(path));

        WriteFile(7);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var after = await TorrentBuilder.BuildAsync(Request(path));

        // Different content must never be served stale hashes.
        Assert.NotEqual(before.InfoHash, after.InfoHash);
        Assert.NotEqual(PiecesOf(before), PiecesOf(after));
    }

    private static byte[] PiecesOf(TorrentResult result)
    {
        var text = Encoding.Latin1.GetString(result.Contents);
        var marker = $"6:pieces{result.PieceCount * 20}:";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return result.Contents[start..(start + result.PieceCount * 20)];
    }
}

public sealed class HashingPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"empprep-pipeline-{Guid.NewGuid():N}");

    public HashingPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ManyPiecesAcrossManyFiles_HashExactlyAsSequentialSha1Would()
    {
        // Enough pieces for several workers to interleave, with file boundaries inside pieces.
        var random = new Random(42);
        var files = new List<TorrentFileEntry>();
        var all = new List<byte>();
        for (var index = 0; index < 7; index++)
        {
            var bytes = new byte[random.Next(30_000, 150_000)];
            random.NextBytes(bytes);
            var path = Path.Combine(_root, $"f{index}.bin");
            await File.WriteAllBytesAsync(path, bytes);
            files.Add(new TorrentFileEntry(path, [$"f{index}.bin"]));
            all.AddRange(bytes);
        }

        const int pieceLength = 16 * 1024;
        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "pack",
            SingleFile = false,
            Files = files,
            AnnounceUrl = "https://tracker.example/announce",
            PieceLength = pieceLength,
        });

        var payload = all.ToArray();
        var expected = new List<byte>();
        for (var offset = 0; offset < payload.Length; offset += pieceLength)
            expected.AddRange(System.Security.Cryptography.SHA1.HashData(payload.AsSpan(offset, Math.Min(pieceLength, payload.Length - offset))));

        var text = Encoding.Latin1.GetString(result.Contents);
        var marker = $"6:pieces{expected.Count}:";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Equal(expected.ToArray(), result.Contents[(start + marker.Length)..(start + marker.Length + expected.Count)]);
    }
}

public sealed class SheetHeaderTests
{
    [Fact]
    public void SheetHeader_NamesTheFileAndItsResolution()
    {
        var header = ScreenshotService.SheetHeader(new PrepMediaInfo
        {
            FileName = "scene.mp4", Width = 1920, Height = 1080, VideoCodec = "h264", FrameRate = 29.97,
            DurationSeconds = 374, FileSizeBytes = 83_110_000,
        });

        Assert.Equal("scene.mp4  ·  1920x1080 (1080p)  ·  h264 29.97 fps  ·  6m 14s  ·  79.26 MiB", header);
    }

    [Fact]
    public void SheetHeader_Shows2160pAs4K()
        => Assert.Contains("3840x2160 (4K)", ScreenshotService.SheetHeader(new PrepMediaInfo { FileName = "a.mp4", Width = 3840, Height = 2160 }));

    [Fact]
    public void FitHeader_LeavesShortTextAlone()
        => Assert.Equal("a.mp4  ·  1920x1080", ScreenshotService.FitHeader("a.mp4  ·  1920x1080", 100));

    [Fact]
    public void FitHeader_ShortensTheNameButKeepsTheDetails()
    {
        var header = "A very long file name that goes on and on and on.mp4  ·  1920x1080 (1080p)  ·  6m 14s";
        var fitted = ScreenshotService.FitHeader(header, 60);

        Assert.True(fitted.Length <= 60, fitted);
        Assert.EndsWith("  ·  1920x1080 (1080p)  ·  6m 14s", fitted);
        Assert.StartsWith("A very", fitted);
        Assert.Contains("…", fitted);
        Assert.Contains(".mp4", fitted);
    }

    [Fact]
    public void HeaderFilter_ReadsTheTextFromAFileSoNamesNeedNoEscaping()
    {
        var textFile = Path.Combine(Path.GetTempPath(), $"empprep-header-{Guid.NewGuid():N}.txt");
        try
        {
            // Quotes, colons, commas and percent signs all mean something in an ffmpeg filter.
            var filter = ScreenshotService.HeaderFilter("It's 100%: a, b.mp4", 1940, null, textFile);

            Assert.Contains("textfile=", filter);
            Assert.Contains("expansion=none", filter);
            Assert.DoesNotContain("It's", filter);
            Assert.Equal("It's 100%: a, b.mp4", File.ReadAllText(textFile));
            // Written without a byte-order mark, which drawtext would draw as a glyph.
            Assert.NotEqual(0xEF, File.ReadAllBytes(textFile)[0]);
        }
        finally
        {
            File.Delete(textFile);
        }
    }
}
