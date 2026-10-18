using System.Runtime.InteropServices;

namespace EmpPrep.Services;

/// <summary>
/// Creates hard links, for building a folder-shaped torrent without a second copy of the payload.
/// </summary>
/// <remarks>
/// <para>
/// A hard link is an additional directory entry pointing at the same bytes on disk. Linking a 6 GB
/// file into a release-named folder costs a directory entry, not 6 GB, and the torrent client can
/// seed from that folder while Cove keeps using the original path. That is what makes a renamed,
/// folder-style release possible under the "no duplicate files" requirement.
/// </para>
/// <para>
/// The constraint is that a hard link cannot cross volumes — on Windows not even between two
/// partitions of the same physical disk. Callers get a clear error naming the volumes rather than a
/// raw Win32 code, because the fix is to move the staging directory, not to retry.
/// </para>
/// </remarks>
public static class HardLink
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    /// <summary>
    /// Hard-link <paramref name="existingPath"/> to <paramref name="linkPath"/>.
    /// </summary>
    /// <remarks>
    /// An existing link at the destination is replaced only when it already points at the same
    /// content; anything else is left alone and reported, so a stale staging directory can never
    /// cause an unrelated file to be deleted.
    /// </remarks>
    public static void Create(string existingPath, string linkPath)
    {
        if (!File.Exists(existingPath))
            throw new FileNotFoundException($"Cannot link a file that is not on disk: {existingPath}", existingPath);

        var directory = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(linkPath))
        {
            if (IsSameContent(existingPath, linkPath))
                return;

            throw new IOException(
                $"A different file already exists at the staging path {linkPath}. Remove it or pick another staging directory.");
        }

        AssertSameVolume(existingPath, linkPath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            {
                throw new IOException(
                    $"Could not hard-link {existingPath} to {linkPath}.",
                    Marshal.GetLastWin32Error());
            }

            return;
        }

        if (link(existingPath, linkPath) != 0)
        {
            throw new IOException(
                $"Could not hard-link {existingPath} to {linkPath}.",
                Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Check whether two paths can be hard-linked, so the UI can warn before a prep run rather than
    /// failing partway through.
    /// </summary>
    public static bool CanLink(string existingPath, string targetDirectory)
    {
        try
        {
            AssertSameVolume(existingPath, Path.Combine(targetDirectory, "probe"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void AssertSameVolume(string existingPath, string linkPath)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(existingPath));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(linkPath));

        if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Hard links cannot cross volumes: the media is on {sourceRoot} but the staging directory is on {targetRoot}. "
                + "Point the staging directory at the same drive as the media, or turn hard-link staging off to hash the file in place.");
        }
    }

    /// <summary>
    /// Whether two paths already refer to the same file. Compared by size and write time: a full
    /// content comparison would mean reading gigabytes to answer a question about staging layout.
    /// </summary>
    private static bool IsSameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        return leftInfo.Length == rightInfo.Length
            && leftInfo.LastWriteTimeUtc == rightInfo.LastWriteTimeUtc;
    }
}
