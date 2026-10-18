using System.Diagnostics;
using System.Runtime.InteropServices;

using Cove.Core.Interfaces;

namespace EmpPrep.Services;

/// <summary>
/// Finds the ffmpeg and ffprobe binaries the same way the host does: the configured path first,
/// then PATH.
/// </summary>
/// <remarks>
/// Cove's own thumbnail service resolves these internally, but it lives in the host assembly and is
/// not part of the extension ABI, so the lookup is repeated here against the shared
/// <see cref="CoveConfiguration"/>. That keeps the extension using whatever binary the user already
/// pointed Cove at, rather than a second copy with different codec support.
/// </remarks>
public sealed class FfmpegLocator(CoveConfiguration config)
{
    private string? _ffmpeg;
    private string? _ffprobe;
    private bool _searchedFfmpeg;
    private bool _searchedFfprobe;
    private readonly object _gate = new();

    public string? FfmpegPath => Resolve(ref _ffmpeg, ref _searchedFfmpeg, config.FfmpegPath, "ffmpeg");

    public string? FfprobePath => Resolve(ref _ffprobe, ref _searchedFfprobe, config.FfprobePath, "ffprobe");

    /// <summary>Resolve ffmpeg or throw a message that tells the user what to fix.</summary>
    public string RequireFfmpeg()
        => FfmpegPath ?? throw new InvalidOperationException(
            "ffmpeg was not found. Set its path in Cove's settings or put it on PATH — EMP Prep needs it to extract screenshots.");

    private string? Resolve(ref string? cached, ref bool searched, string? configured, string binaryName)
    {
        lock (_gate)
        {
            if (searched)
                return cached;

            searched = true;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                // The configured value may be either the executable or the directory holding it.
                if (File.Exists(configured))
                {
                    cached = configured;
                    return cached;
                }

                if (Directory.Exists(configured))
                {
                    var candidate = Path.Combine(configured, ExecutableName(binaryName));
                    if (File.Exists(candidate))
                    {
                        cached = candidate;
                        return cached;
                    }
                }
            }

            cached = SearchPath(binaryName);
            return cached;
        }
    }

    private static string ExecutableName(string binaryName)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{binaryName}.exe" : binaryName;

    private static string? SearchPath(string binaryName)
    {
        var executable = ExecutableName(binaryName);
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing the lookup over.
            }
        }

        return null;
    }

    /// <summary>
    /// Run a binary to completion, returning its exit code and captured output.
    /// </summary>
    /// <remarks>
    /// stdout and stderr are drained concurrently with the wait. ffmpeg writes progress to stderr
    /// continuously, and a process whose pipe buffer fills up while the parent waits for exit
    /// deadlocks — a hang that only shows on longer files.
    /// </remarks>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
