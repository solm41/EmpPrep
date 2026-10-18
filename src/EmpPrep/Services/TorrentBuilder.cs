using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace EmpPrep.Services;

/// <summary>One file that will be listed in the torrent, with the path it gets inside it.</summary>
/// <param name="SourcePath">Absolute path on disk. Read where it already sits — never copied.</param>
/// <param name="TorrentPath">Path components relative to the torrent's root name.</param>
public sealed record TorrentFileEntry(string SourcePath, IReadOnlyList<string> TorrentPath)
{
    public long Length { get; init; }
}

public sealed record TorrentRequest
{
    /// <summary>The torrent's root name — the release name, and the folder/file a client expects.</summary>
    public required string Name { get; init; }
    public required IReadOnlyList<TorrentFileEntry> Files { get; init; }
    /// <summary>True when the torrent describes a single file rather than a directory.</summary>
    public required bool SingleFile { get; init; }
    public required string AnnounceUrl { get; init; }
    /// <summary>Tracker-specific cross-seed tag written to info.source (e.g. "EMP").</summary>
    public string? Source { get; init; }
    public string? Comment { get; init; }
    /// <summary>Explicit piece length in bytes; when null a size is chosen from the total payload.</summary>
    public int? PieceLength { get; init; }
    public string CreatedBy { get; init; } = "Cove EMP Prep";

    /// <summary>
    /// Where to remember piece hashes between builds. When set, rebuilding a torrent over files whose
    /// sizes and modification times have not changed reuses the hashes instead of re-reading every
    /// byte — for a large pack on a network share, the difference between seconds and many minutes.
    /// </summary>
    public string? PieceCacheDirectory { get; init; }
}

public sealed record TorrentResult(
    byte[] Contents,
    string InfoHash,
    int PieceLength,
    int PieceCount,
    long TotalBytes,
    string Name);

/// <summary>
/// Builds a v1 .torrent from files that stay exactly where they are.
/// </summary>
/// <remarks>
/// Nothing here writes to, moves, or copies the payload: files are opened read-only, hashed as a
/// single logical stream in listed order, and the resulting metainfo is written elsewhere. Seeding
/// is then a matter of pointing the client at the existing directory, so the library holds one copy
/// of the content and the torrent adds only a few KB of metadata.
///
/// Every torrent is marked private. That is deliberately not a caller option: an EMP upload with a
/// public infohash is a rules problem for the uploader, and the flag is part of the info dictionary,
/// so discovering it was missing after the fact means rehashing and re-uploading under a new hash.
/// </remarks>
public static class TorrentBuilder
{
    private const int MinPieceLength = 16 * 1024;        // 16 KiB — smallest a client will accept
    /// <summary>
    /// 8 MiB: EMP's upload checker rejects a torrent with larger pieces. Every size choice here —
    /// automatic or overridden — is capped at it.
    /// </summary>
    public const int MaxPieceLength = 8 * 1024 * 1024;
    private const int TargetPieceCount = 1500;           // keeps the .torrent itself small

    /// <summary>
    /// Pick a piece length: the smallest power of two that keeps the piece count at or under
    /// <see cref="TargetPieceCount"/>, clamped to what clients accept.
    /// </summary>
    /// <remarks>
    /// This lands on the sizes private trackers conventionally recommend — about 512 KiB for a
    /// 700 MB file, 1 MiB at 1 GiB, 2 MiB at 2 GiB, 4 MiB at 4 GiB and 8 MiB from 8 GiB — keeping
    /// every torrent between roughly 750 and 1500 pieces. Above ~12 GiB the 8 MiB cap wins, so large
    /// packs simply have more pieces (a 67 GB pack has about 8,000, a ~160 KB .torrent). Fewer, larger pieces
    /// would make a single corrupt block cost more to re-download; many more would bloat the
    /// .torrent (20 bytes of hash per piece) and the per-piece overhead in clients.
    /// </remarks>
    public static int ChoosePieceLength(long totalBytes)
    {
        var length = MinPieceLength;
        while (length < MaxPieceLength && totalBytes / length > TargetPieceCount)
            length *= 2;
        return length;
    }

    /// <summary>
    /// Coerce a requested piece length to a power of two within the range clients accept.
    /// </summary>
    /// <remarks>
    /// Clients assume a power-of-two piece length and several reject anything else outright, so an
    /// override such as 1000 KiB is rounded to the nearest valid size rather than written as-is.
    /// </remarks>
    public static int NormalizePieceLength(long requestedBytes)
    {
        if (requestedBytes <= MinPieceLength)
            return MinPieceLength;
        if (requestedBytes >= MaxPieceLength)
            return MaxPieceLength;

        var lower = MinPieceLength;
        while (lower * 2L <= requestedBytes)
            lower *= 2;

        var upper = lower * 2;
        // Nearest power of two; a tie rounds up.
        return requestedBytes - lower < upper - requestedBytes ? lower : upper;
    }

    public static async Task<TorrentResult> BuildAsync(
        TorrentRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files.Count == 0)
            throw new ArgumentException("A torrent needs at least one file.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AnnounceUrl))
            throw new ArgumentException("An announce URL is required. Set it in EMP Prep settings.", nameof(request));
        if (request.SingleFile && request.Files.Count != 1)
            throw new ArgumentException("A single-file torrent must list exactly one file.", nameof(request));

        var files = new List<TorrentFileEntry>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var fileInfo = new FileInfo(file.SourcePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException($"Cannot hash a file that is not on disk: {file.SourcePath}", file.SourcePath);
            files.Add(file with { Length = fileInfo.Length });
        }

        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes == 0)
            throw new InvalidOperationException("The selected files are all empty; there is nothing to hash.");

        var pieceLength = request.PieceLength is > 0
            ? NormalizePieceLength(request.PieceLength.Value)
            : ChoosePieceLength(totalBytes);
        var pieces = await CachedPiecesAsync(request.PieceCacheDirectory, files, pieceLength, totalBytes, progress, ct);

        var info = BencodeValue.Dict()
            .Add("name", BencodeValue.Str(request.Name))
            .Add("piece length", pieceLength)
            .Add("pieces", BencodeValue.Bytes(pieces))
            // Private tracker torrent: no DHT, no PEX, no local peer discovery.
            .Add("private", 1);

        info.AddIfPresent("source", request.Source);

        if (request.SingleFile)
        {
            info.Add("length", files[0].Length);
        }
        else
        {
            info.Add("files", BencodeValue.List(files.Select(file =>
                (BencodeValue)BencodeValue.Dict()
                    .Add("length", file.Length)
                    .Add("path", BencodeValue.List(file.TorrentPath.Select(BencodeValue.Str))))));
        }

        var metainfo = BencodeValue.Dict()
            .Add("announce", BencodeValue.Str(request.AnnounceUrl))
            .Add("announce-list", BencodeValue.List([BencodeValue.List([BencodeValue.Str(request.AnnounceUrl)])]))
            .Add("creation date", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Add("info", info);

        metainfo.AddIfPresent("created by", request.CreatedBy);
        metainfo.AddIfPresent("comment", request.Comment);

        var infoHash = Convert.ToHexStringLower(SHA1.HashData(info.Encode()));

        return new TorrentResult(
            metainfo.Encode(),
            infoHash,
            pieceLength,
            pieces.Length / 20,
            totalBytes,
            request.Name);
    }

    /// <summary>
    /// Piece hashes for the payload, reused from a previous build when every file is unchanged.
    /// </summary>
    /// <remarks>
    /// The cache key is the piece length plus each file's path, size and modification time, in order
    /// — the same "has this file changed" test torrent clients use to skip rechecks. Anything that
    /// alters a byte also alters the size or the time, so a stale hash cannot be served; if in doubt
    /// the cache simply misses and the payload is hashed again.
    /// </remarks>
    private static async Task<byte[]> CachedPiecesAsync(
        string? cacheDirectory,
        IReadOnlyList<TorrentFileEntry> files,
        int pieceLength,
        long totalBytes,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        string? cachePath = null;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            var key = new StringBuilder().Append(pieceLength);
            foreach (var file in files)
            {
                var info = new FileInfo(file.SourcePath);
                key.Append('|').Append(Path.GetFullPath(file.SourcePath).ToUpperInvariant())
                    .Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }

            var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
            cachePath = Path.Combine(cacheDirectory, name + ".pieces");

            var expectedLength = (int)((totalBytes + pieceLength - 1) / pieceLength) * 20;
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length == expectedLength)
            {
                progress?.Report(1);
                return await File.ReadAllBytesAsync(cachePath, ct);
            }
        }

        var pieces = await HashPiecesAsync(files, pieceLength, totalBytes, progress, ct);

        if (cachePath is not null)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory!);
                await File.WriteAllBytesAsync(cachePath, pieces, ct);
            }
            catch (IOException)
            {
                // A cache that cannot be written only costs the next rebuild its shortcut.
            }
        }

        return pieces;
    }

    /// <summary>
    /// SHA-1 every piece of the payload. Pieces are taken from the concatenation of all files in
    /// listed order, so a piece can straddle a file boundary.
    /// </summary>
    /// <remarks>
    /// Reading and hashing overlap: one reader streams whole pieces into pooled buffers while
    /// several workers hash them. Done in turn, the read (the network, for a share) sat idle during
    /// every hash and the CPU during every read. The reader stays at most a few pieces ahead, so
    /// memory is bounded however large the payload is.
    /// </remarks>
    private static async Task<byte[]> HashPiecesAsync(
        IReadOnlyList<TorrentFileEntry> files,
        int pieceLength,
        long totalBytes,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var pieceCount = (int)((totalBytes + pieceLength - 1) / pieceLength);
        var hashes = new byte[pieceCount * 20];
        var workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        var channel = Channel.CreateBounded<(int Index, byte[] Buffer, int Length)>(
            new BoundedChannelOptions(workers * 2) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        long hashedBytes = 0;
        var lastReported = -1d;
        var reportGate = new object();

        void ReportProgress(int length)
        {
            if (progress is null)
                return;
            var fraction = Math.Round((double)Interlocked.Add(ref hashedBytes, length) / totalBytes, 3);
            // Report under the lock so values are handed over in increasing order.
            lock (reportGate)
            {
                if (fraction <= lastReported)
                    return;
                lastReported = fraction;
                progress.Report(fraction);
            }
        }

        using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var hashing = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var (index, buffer, length) in channel.Reader.ReadAllAsync(failure.Token))
                {
                    try
                    {
                        SHA1.HashData(buffer.AsSpan(0, length), hashes.AsSpan(index * 20, 20));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    ReportProgress(length);
                }
            }
            catch
            {
                // A failed worker must stop the reader too, or it would wait forever on a full queue.
                failure.Cancel();
                throw;
            }
        }, failure.Token)).ToList();

        try
        {
            var pieceIndex = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(pieceLength);
            var filled = 0;

            foreach (var file in files)
            {
                await using var stream = new FileStream(
                    file.SourcePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        // Read-only, and tolerant of the file being read or written elsewhere: Cove may
                        // be streaming the same file to a player while this runs.
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        // Reads go straight into whole-piece buffers; a second buffer only adds a copy.
                        BufferSize = 0,
                    });

                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled, pieceLength - filled), failure.Token);
                    if (read == 0)
                        break;

                    filled += read;
                    if (filled == pieceLength)
                    {
                        await channel.Writer.WriteAsync((pieceIndex++, buffer, filled), failure.Token);
                        buffer = ArrayPool<byte>.Shared.Rent(pieceLength);
                        filled = 0;
                    }
                }
            }

            // Final short piece.
            if (filled > 0)
                await channel.Writer.WriteAsync((pieceIndex, buffer, filled), failure.Token);
            else
                ArrayPool<byte>.Shared.Return(buffer);

            channel.Writer.Complete();
            await Task.WhenAll(hashing);
        }
        catch
        {
            // Stop the workers and surface the original error rather than a cancellation.
            channel.Writer.TryComplete();
            failure.Cancel();
            try { await Task.WhenAll(hashing); } catch { /* the first error is the one that matters */ }
            throw;
        }

        return hashes;
    }

    /// <summary>
    /// Sanitise a release name into something safe to use as the torrent root and output filename.
    /// </summary>
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        var cleaned = builder.ToString().Trim('.', ' ');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }
}
