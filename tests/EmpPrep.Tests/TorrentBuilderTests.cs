using System.Security.Cryptography;
using System.Text;

using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Tests for the torrent metainfo encoder.
/// </summary>
/// <remarks>
/// These assert the encoding byte-for-byte rather than round-tripping through a decoder. An
/// infohash is the SHA-1 of the encoded info dictionary, so a torrent that decodes fine but encodes
/// differently from every other client is still wrong — the tracker would not recognise it. Checking
/// the bytes is the only way to catch that.
/// </remarks>
public sealed class BencodeTests
{
    [Fact]
    public void Integers_AreDelimitedAndUnpadded()
    {
        Assert.Equal("i42e", Encode(BencodeValue.Int(42)));
        Assert.Equal("i0e", Encode(BencodeValue.Int(0)));
        Assert.Equal("i-7e", Encode(BencodeValue.Int(-7)));
    }

    [Fact]
    public void Strings_ArePrefixedWithTheirByteLength()
    {
        Assert.Equal("4:spam", Encode(BencodeValue.Str("spam")));
        Assert.Equal("0:", Encode(BencodeValue.Str("")));
    }

    [Fact]
    public void Strings_UseByteLengthNotCharacterLength()
    {
        // "é" is two bytes in UTF-8. A character count here would produce an unparseable string and
        // a torrent no client could read — release names routinely contain non-ASCII.
        Assert.Equal("2:é", Encode(BencodeValue.Str("é")));
        Assert.Equal("3:ué", Encode(BencodeValue.Str("ué")));
        Assert.Equal("6:日本", Encode(BencodeValue.Str("日本")));
    }

    [Fact]
    public void Lists_NestInOrder()
        => Assert.Equal("l4:spami1ee", Encode(BencodeValue.List([BencodeValue.Str("spam"), BencodeValue.Int(1)])));

    [Fact]
    public void Dictionaries_EmitKeysInByteOrderRegardlessOfInsertionOrder()
    {
        // The spec requires sorted keys, and the infohash depends on it: the same logical torrent
        // built with keys added in a different order must encode identically.
        var dictionary = BencodeValue.Dict()
            .Add("zebra", BencodeValue.Int(1))
            .Add("apple", BencodeValue.Int(2))
            .Add("Banana", BencodeValue.Int(3));

        // Uppercase 'B' (0x42) sorts before lowercase letters in byte order.
        Assert.Equal("d6:Bananai3e5:applei2e5:zebrai1ee", Encode(dictionary));
    }

    [Fact]
    public void Dictionaries_SortPrefixKeysBeforeLongerKeys()
    {
        var dictionary = BencodeValue.Dict()
            .Add("piece length", BencodeValue.Int(1))
            .Add("pieces", BencodeValue.Int(2))
            .Add("private", BencodeValue.Int(3));

        // "piece length" < "pieces" because ' ' (0x20) < 's' (0x73).
        var encoded = Encode(dictionary);
        Assert.True(
            encoded.IndexOf("12:piece length", StringComparison.Ordinal)
                < encoded.IndexOf("6:pieces", StringComparison.Ordinal),
            encoded);
        Assert.True(
            encoded.IndexOf("6:pieces", StringComparison.Ordinal)
                < encoded.IndexOf("7:private", StringComparison.Ordinal),
            encoded);
    }

    private static string Encode(BencodeValue value)
    {
        var dictionary = value as BencodeDictionary;
        if (dictionary is not null)
            return Encoding.UTF8.GetString(dictionary.Encode());

        // Non-dictionary values are encoded by wrapping them, then stripping the wrapper.
        var wrapped = Encoding.UTF8.GetString(BencodeValue.Dict().Add("k", value).Encode());
        return wrapped["d1:k".Length..^1];
    }
}

public sealed class TorrentBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"empprep-torrent-{Guid.NewGuid():N}");

    public TorrentBuilderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string name, byte[] contents)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)(index % 251);
        return bytes;
    }

    [Fact]
    public async Task SingleFileTorrent_IsAlwaysMarkedPrivate()
    {
        // The whole point of a private-tracker torrent. This is asserted rather than trusted because
        // the flag changes the infohash: noticing it was missing after upload means re-hashing and
        // re-uploading, and in the meantime the swarm is announcing to DHT.
        var path = WriteFile("clip.mp4", Pattern(40_000));

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/passkey/announce",
        });

        var encoded = Encoding.Latin1.GetString(result.Contents);
        Assert.Contains("7:privatei1e", encoded);
    }

    [Fact]
    public async Task SingleFileTorrent_CarriesLengthAndNoFilesList()
    {
        var path = WriteFile("clip.mp4", Pattern(40_000));

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
        });

        var encoded = Encoding.Latin1.GetString(result.Contents);
        Assert.Contains("6:lengthi40000e", encoded);
        Assert.DoesNotContain("5:filesl", encoded);
        Assert.Equal(40_000, result.TotalBytes);
    }

    [Fact]
    public async Task SourceTag_IsIncludedWhenSetAndOmittedWhenBlank()
    {
        var path = WriteFile("clip.mp4", Pattern(1_000));

        var withSource = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
            Source = "EMP",
        });

        var withoutSource = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
            Source = "   ",
        });

        Assert.Contains("6:source3:EMP", Encoding.Latin1.GetString(withSource.Contents));
        Assert.DoesNotContain("6:source", Encoding.Latin1.GetString(withoutSource.Contents));

        // The source tag is inside the info dict, so it must change the infohash — that is what
        // makes the same content cross-seedable as a distinct torrent per tracker.
        Assert.NotEqual(withSource.InfoHash, withoutSource.InfoHash);
    }

    [Fact]
    public async Task PieceHashes_MatchSha1OfEachPiece()
    {
        // 3.5 pieces at a 16 KiB piece length, so this covers both whole pieces and the short tail.
        const int pieceLength = 16 * 1024;
        var contents = Pattern(pieceLength * 3 + 1_234);
        var path = WriteFile("clip.mp4", contents);

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
            PieceLength = pieceLength,
        });

        Assert.Equal(4, result.PieceCount);

        var pieces = ExtractPieces(result.Contents, result.PieceCount);
        for (var index = 0; index < result.PieceCount; index++)
        {
            var offset = index * pieceLength;
            var length = Math.Min(pieceLength, contents.Length - offset);
            var expected = SHA1.HashData(contents.AsSpan(offset, length));
            Assert.Equal(expected, pieces[index]);
        }
    }

    [Fact]
    public async Task MultiFileTorrent_HashesPiecesAcrossTheFileBoundary()
    {
        // The case a naive per-file implementation gets wrong: with a 16 KiB piece length and a
        // 20 KiB first file, piece 1 is the tail of file A followed by the head of file B.
        const int pieceLength = 16 * 1024;
        var first = Pattern(20 * 1024);
        var second = Pattern(12 * 1024);

        var firstPath = WriteFile("a.mp4", first);
        var secondPath = WriteFile("b.mp4", second);

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "Release",
            SingleFile = false,
            Files =
            [
                new TorrentFileEntry(firstPath, ["a.mp4"]),
                new TorrentFileEntry(secondPath, ["b.mp4"]),
            ],
            AnnounceUrl = "https://tracker.example/announce",
            PieceLength = pieceLength,
        });

        var concatenated = first.Concat(second).ToArray();
        Assert.Equal(32 * 1024, result.TotalBytes);
        Assert.Equal(2, result.PieceCount);

        var pieces = ExtractPieces(result.Contents, result.PieceCount);
        Assert.Equal(SHA1.HashData(concatenated.AsSpan(0, pieceLength)), pieces[0]);
        Assert.Equal(SHA1.HashData(concatenated.AsSpan(pieceLength)), pieces[1]);

        var encoded = Encoding.Latin1.GetString(result.Contents);
        Assert.Contains("5:filesl", encoded);
        Assert.DoesNotContain("d6:lengthi32768e", encoded);
    }

    [Fact]
    public async Task InfoHash_IsStableForIdenticalInputAndSensitiveToName()
    {
        var path = WriteFile("clip.mp4", Pattern(20_000));

        TorrentRequest Request(string name) => new()
        {
            Name = name,
            SingleFile = true,
            Files = [new TorrentFileEntry(path, [name])],
            AnnounceUrl = "https://tracker.example/announce",
            Source = "EMP",
            PieceLength = 16 * 1024,
        };

        var first = await TorrentBuilder.BuildAsync(Request("clip.mp4"));
        var second = await TorrentBuilder.BuildAsync(Request("clip.mp4"));
        var renamed = await TorrentBuilder.BuildAsync(Request("other.mp4"));

        Assert.Equal(first.InfoHash, second.InfoHash);
        Assert.NotEqual(first.InfoHash, renamed.InfoHash);
        Assert.Equal(40, first.InfoHash.Length);
    }

    [Fact]
    public async Task Build_DoesNotModifyOrCopyThePayload()
    {
        // The requirement that shapes the whole extension: hashing must leave the library alone.
        var contents = Pattern(50_000);
        var path = WriteFile("clip.mp4", contents);
        var before = File.GetLastWriteTimeUtc(path);
        var filesBefore = Directory.GetFiles(_directory).Length;

        await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
        });

        Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(filesBefore, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task Build_RequiresAnAnnounceUrl()
    {
        var path = WriteFile("clip.mp4", Pattern(1_000));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "  ",
        }));

        Assert.Contains("announce URL", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_ReportsProgressToCompletion()
    {
        var path = WriteFile("clip.mp4", Pattern(16 * 1024 * 5));
        var reported = new List<double>();

        await TorrentBuilder.BuildAsync(
            new TorrentRequest
            {
                Name = "clip.mp4",
                SingleFile = true,
                Files = [new TorrentFileEntry(path, ["clip.mp4"])],
                AnnounceUrl = "https://tracker.example/announce",
                PieceLength = 16 * 1024,
            },
            new Progress<double>(reported.Add));

        // Progress is consumed by the page's per-item bar, so it must actually reach 1.
        Assert.NotEmpty(reported);
        Assert.Equal(1d, reported[^1], 3);
    }

    [Fact]
    public async Task Build_RejectsAMissingFile()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "gone.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(Path.Combine(_directory, "gone.mp4"), ["gone.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
        }));

    [Theory]
    [InlineData(1_000L, 16 * 1024)]                   // tiny: the floor
    [InlineData(700L * 1024 * 1024, 512 * 1024)]      // ~700 MB
    [InlineData(8L * 1024 * 1024 * 1024, 8 * 1024 * 1024)]
    public void ChoosePieceLength_StaysAPowerOfTwoInRange(long totalBytes, int expected)
    {
        var chosen = TorrentBuilder.ChoosePieceLength(totalBytes);
        Assert.Equal(expected, chosen);
        Assert.Equal(0, chosen & (chosen - 1));
        Assert.InRange(chosen, 16 * 1024, TorrentBuilder.MaxPieceLength);
    }

    [Theory]
    [InlineData(80L * 1024 * 1024, 64)]            // an 80 MiB clip, like a short scene
    [InlineData(350L * 1024 * 1024, 256)]
    [InlineData(700L * 1024 * 1024, 512)]
    [InlineData(1024L * 1024 * 1024, 1024)]        // 1 GiB  -> 1 MiB
    [InlineData(2048L * 1024 * 1024, 2048)]        // 2 GiB  -> 2 MiB
    [InlineData(4096L * 1024 * 1024, 4096)]        // 4 GiB  -> 4 MiB
    [InlineData(8192L * 1024 * 1024, 8192)]        // 8 GiB  -> 8 MiB
    [InlineData(20L * 1024 * 1024 * 1024, 8192)]   // 20 GiB -> 8 MiB, EMP's cap
    [InlineData(67L * 1000 * 1000 * 1000, 8192)]   // a 67 GB megapack: still 8 MiB
    public void ChoosePieceLength_MatchesTheConventionalSizeTable(long totalBytes, int expectedKiB)
    {
        var chosen = TorrentBuilder.ChoosePieceLength(totalBytes);
        Assert.Equal(expectedKiB * 1024, chosen);

        // Below the cap, the piece count stays in the conventional band: small enough to keep the
        // .torrent light, large enough that one bad piece is cheap to re-fetch.
        if (chosen < TorrentBuilder.MaxPieceLength)
            Assert.InRange(totalBytes / chosen, 750, 1500);
    }

    [Theory]
    [InlineData(1000 * 1024, 1024 * 1024)]         // 1000 KiB rounds to 1 MiB
    [InlineData(300 * 1024, 256 * 1024)]
    [InlineData(384 * 1024, 512 * 1024)]           // exactly between 256 and 512: rounds up
    [InlineData(512 * 1024, 512 * 1024)]           // already valid: unchanged
    [InlineData(1, 16 * 1024)]                     // below the floor
    [InlineData(64L * 1024 * 1024, 8 * 1024 * 1024)]  // above EMP's cap
    [InlineData(16L * 1024 * 1024, 8 * 1024 * 1024)]  // 16 MiB is rejected by EMP, so capped
    public void NormalizePieceLength_RoundsOverridesToAValidPowerOfTwo(long requested, int expected)
    {
        var normalized = TorrentBuilder.NormalizePieceLength(requested);
        Assert.Equal(expected, normalized);
        Assert.Equal(0, normalized & (normalized - 1));
    }

    [Fact]
    public async Task Build_NormalizesAnInvalidPieceLengthOverride()
    {
        // A non-power-of-two piece length is written as-is by a naive builder and rejected by clients.
        var path = WriteFile("clip.mp4", Pattern(200_000));
        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "clip.mp4",
            SingleFile = true,
            Files = [new TorrentFileEntry(path, ["clip.mp4"])],
            AnnounceUrl = "https://tracker.example/announce",
            PieceLength = 20_000,
        });

        Assert.Equal(16 * 1024, result.PieceLength);
    }

    [Theory]
    [InlineData(0, 0)]        // automatic stays automatic
    [InlineData(-5, 0)]
    [InlineData(1000, 1024)]
    [InlineData(4096, 4096)]
    [InlineData(16384, 8192)]
    [InlineData(100000, 8192)]
    public void Settings_NormalizeThePieceLengthOverride(int input, int expected)
        => Assert.Equal(expected, new EmpPrep.Models.EmpPrepSettings { PieceLengthKiB = input }.Normalized().PieceLengthKiB);

    [Fact]
    public void SanitizeName_StripsPathSeparatorsAndTrailingDots()
    {
        Assert.DoesNotContain('/', TorrentBuilder.SanitizeName("Studio/Title"));
        Assert.DoesNotContain('\\', TorrentBuilder.SanitizeName("Studio\\Title"));
        Assert.Equal("untitled", TorrentBuilder.SanitizeName("   "));
        Assert.False(TorrentBuilder.SanitizeName("Title...").EndsWith('.'));
    }

    /// <summary>Pull the raw 20-byte piece hashes back out of an encoded torrent.</summary>
    private static List<byte[]> ExtractPieces(byte[] torrent, int pieceCount)
    {
        var marker = Encoding.ASCII.GetBytes($"6:pieces{pieceCount * 20}:");
        var start = IndexOf(torrent, marker);
        Assert.True(start >= 0, "The encoded torrent has no pieces entry.");

        var offset = start + marker.Length;
        var pieces = new List<byte[]>(pieceCount);
        for (var index = 0; index < pieceCount; index++)
            pieces.Add(torrent[(offset + index * 20)..(offset + (index + 1) * 20)]);

        return pieces;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
                return index;
        }

        return -1;
    }
}
