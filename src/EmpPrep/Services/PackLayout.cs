namespace EmpPrep.Services;

/// <summary>One file placed in a pack folder: where it comes from and its path inside the pack.</summary>
/// <param name="SourcePath">The existing file. Videos are hard-linked from here; screens are copied.</param>
/// <param name="RelativePath">Path components under the pack folder, e.g. ["Screens", "a.jpg"].</param>
/// <param name="IsVideo">True for a library video (hard-linked), false for a generated screen (copied).</param>
public sealed record PackEntry(string SourcePath, IReadOnlyList<string> RelativePath, bool IsVideo);

/// <summary>
/// Plans and checks the on-disk shape of a pack: a folder named for the pack, holding a hard link to
/// every video and a Screens folder with one contact sheet per video.
/// </summary>
/// <remarks>
/// <para>
/// A multi-file torrent describes a folder and the files inside it, so the files have to exist
/// together in one folder for a client to seed them. The library's videos live wherever Cove keeps
/// them, so the pack folder is built from hard links: second names for the same data. Nothing in the
/// library is moved, renamed or copied, and a pack costs no extra disk space for its videos.
/// </para>
/// <para>
/// EMP's pack rules require screens inside the torrent, in their own folder, for every video. The
/// contact sheets are small generated images, so they are simply copied into Screens.
/// </para>
/// </remarks>
public static class PackLayout
{
    public const string ScreensFolder = "Screens";

    /// <summary>
    /// Lay out a pack. Videos keep their own file names; two videos with the same name get " (2)",
    /// " (3)" and so on, since a torrent cannot hold two files at one path. Each video's contact sheet
    /// is named after the video's name in the pack, so the two sort and match together.
    /// </summary>
    public static IReadOnlyList<PackEntry> Plan(IReadOnlyList<(string VideoPath, string? SheetPath)> scenes)
    {
        var entries = new List<PackEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (videoPath, sheetPath) in scenes)
        {
            var fileName = UniqueName(TorrentBuilder.SanitizeName(Path.GetFileName(videoPath)), usedNames);
            entries.Add(new PackEntry(videoPath, [fileName], IsVideo: true));

            if (!string.IsNullOrWhiteSpace(sheetPath))
            {
                var sheetName = Path.GetFileNameWithoutExtension(fileName) + Path.GetExtension(sheetPath);
                entries.Add(new PackEntry(sheetPath, [ScreensFolder, sheetName], IsVideo: false));
            }
        }

        return entries;
    }

    /// <summary>Where each entry lands on disk under <paramref name="folder"/>.</summary>
    public static IReadOnlyList<string> Targets(string folder, IReadOnlyList<PackEntry> entries)
        => entries.Select(entry => Path.Combine([folder, .. entry.RelativePath])).ToList();

    /// <summary>
    /// Create the pack folder: hard-link each video, copy each screen. Safe to repeat — a link that
    /// already points at the same video is left as it is, and screens are refreshed.
    /// </summary>
    public static IReadOnlyList<string> Materialize(string folder, IReadOnlyList<PackEntry> entries, CancellationToken ct = default)
    {
        var targets = Targets(folder, entries);
        for (var index = 0; index < entries.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[index];
            var target = targets[index];
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (entry.IsVideo)
                HardLink.Create(entry.SourcePath, target);
            else
                File.Copy(entry.SourcePath, target, overwrite: true);
        }

        return targets;
    }

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
            return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Why the pack folder cannot be used, or null when it is fine.
    /// </summary>
    /// <remarks>
    /// The folder must not sit inside a Cove library path, and no library path may sit inside it. In
    /// either case Cove's scanner would walk the pack folder, find the hard links and import every
    /// video a second time as a duplicate.
    /// </remarks>
    public static string? DescribeFolderProblem(string? packDirectory, IEnumerable<string> libraryPaths)
    {
        if (string.IsNullOrWhiteSpace(packDirectory))
            return "No pack folder is set. Packs cannot be built until one is.";

        string pack;
        try
        {
            pack = Normalize(packDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"The pack folder is not a valid path: {exception.Message}";
        }

        foreach (var library in libraryPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var normalizedLibrary = Normalize(library);
            if (IsSameOrInside(pack, normalizedLibrary))
            {
                return $"The pack folder is inside the Cove library path {library}. Cove would scan it and import every "
                    + "linked video again as a duplicate. Choose a folder outside your library paths.";
            }

            if (IsSameOrInside(normalizedLibrary, pack))
            {
                return $"The Cove library path {library} is inside the pack folder, so Cove would scan the packs. "
                    + "Choose a folder outside your library paths.";
            }
        }

        return null;
    }

    /// <summary>
    /// The videos that cannot be hard-linked into <paramref name="packDirectory"/> because they are on
    /// a different drive. A hard link cannot cross drives — and on Windows not even two partitions of
    /// the same disk — so these would have to be copied, which a pack never does.
    /// </summary>
    public static IReadOnlyList<string> OnOtherDrives(string packDirectory, IEnumerable<string> videoPaths)
    {
        var packRoot = Path.GetPathRoot(Normalize(packDirectory));
        return videoPaths
            .Where(path => !string.Equals(Path.GetPathRoot(Normalize(path)), packRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsSameOrInside(string candidate, string parent)
        => string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
