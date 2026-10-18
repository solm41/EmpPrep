using System.Text.Json;

using Cove.Plugins;

using EmpPrep.Models;

namespace EmpPrep.Services;

/// <summary>
/// Reads and writes the extension's settings blob, and owns the resolved output directory.
/// </summary>
/// <remarks>
/// Registered as a singleton so the scoped request services can reach the extension-level
/// <see cref="IExtensionStore"/>, which the host hands to the extension instance rather than to DI.
/// Settings are cached after the first read and replaced wholesale on save; there is one writer
/// (the settings panel) and reads are frequent.
/// </remarks>
public sealed class EmpPrepSettingsProvider
{
    private const string StoreKey = "settings";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IExtensionStore? _store;
    private EmpPrepSettings? _cached;

    /// <summary>Extension data directory, used when no output directory is configured.</summary>
    public string DataDirectory { get; private set; } = "";

    internal void Attach(IExtensionStore store, string dataDirectory)
    {
        _store = store;
        DataDirectory = dataDirectory;
    }

    public async Task<EmpPrepSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } raced)
                return raced;

            var payload = _store is null ? null : await _store.GetAsync(StoreKey, ct);
            var settings = string.IsNullOrWhiteSpace(payload)
                ? new EmpPrepSettings()
                : Deserialize(payload);

            _cached = settings.Normalized();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EmpPrepSettings> SaveAsync(EmpPrepSettings settings, CancellationToken ct = default)
    {
        var normalized = settings.Normalized();

        await _gate.WaitAsync(ct);
        try
        {
            if (_store is not null)
                await _store.SetAsync(StoreKey, JsonSerializer.Serialize(normalized, JsonOptions), ct);
            _cached = normalized;
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static EmpPrepSettings Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<EmpPrepSettings>(payload, JsonOptions) ?? new EmpPrepSettings();
        }
        catch (JsonException)
        {
            // A store value from an older shape should not stop the extension from loading.
            return new EmpPrepSettings();
        }
    }

    /// <summary>Resolve where prep artifacts are written, creating the directory if needed.</summary>
    public string ResolveOutputDirectory(EmpPrepSettings settings)
    {
        // The data directory Cove hands extensions is its shared extensions folder, and discovery
        // treats every subfolder there as a candidate extension. A dot-prefixed folder follows the
        // convention other extensions use for their data (e.g. ".ai-faces-data").
        var configured = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? Path.Combine(DataDirectory, ".empprep-data", "prep")
            : settings.OutputDirectory;

        Directory.CreateDirectory(configured);
        return configured;
    }

    /// <summary>The configured pack folder, or an error explaining that it has to be set.</summary>
    public static string RequirePackDirectory(EmpPrepSettings settings)
        => string.IsNullOrWhiteSpace(settings.PackDirectory)
            ? throw new EmpPrepValidationException(
                "Set the pack folder in EMP Prep settings first. It must be on the same drive as your videos and outside every Cove library path.")
            : Path.GetFullPath(settings.PackDirectory);
}
