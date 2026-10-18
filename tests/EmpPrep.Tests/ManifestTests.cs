using System.Text.Json;

using Cove.Core.Common;
using Cove.Plugins;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Parses the shipped extension.json with the host's own manifest type.
/// </summary>
/// <remarks>
/// The host deserializes this file before it loads any DLL, and a manifest it cannot parse means the
/// directory is skipped — the extension simply never appears, with nothing in the UI to explain why.
/// Nothing else in the build validates the file, so these tests stand in for the host's parser.
/// </remarks>
public sealed class ManifestTests
{
    private static readonly JsonSerializerOptions HostOptions = new() { PropertyNameCaseInsensitive = true };

    private static string ManifestPath()
    {
        // Walk up from the test binaries to the repo root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmpPrep.Extension.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "EmpPrep", "extension.json");
        Assert.True(File.Exists(path), $"extension.json not found at {path}");
        return path;
    }

    private static ExtensionManifestFile Parse()
    {
        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(File.ReadAllText(ManifestPath()), HostOptions);
        Assert.NotNull(manifest);
        return manifest!;
    }

    [Fact]
    public void Manifest_ParsesWithTheHostsOwnDeserializer()
    {
        // Exactly how ExtensionManager reads it during discovery. A required property missing from
        // any nested object throws here, which is what silently hides an extension from the host.
        var manifest = Parse();

        Assert.Equal("cove.community.empprep", manifest.Id);
        Assert.Equal("EMP Prep", manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
    }

    [Fact]
    public void Manifest_DeclaresTheEntryDllAndBundlesThatShip()
    {
        var manifest = Parse();

        Assert.Equal("EmpPrep.dll", manifest.EntryDll);
        Assert.Equal("dist/bundle.js", manifest.JsBundle);
        Assert.Equal("dist/bundle.css", manifest.CssBundle);

        // The bundles the manifest points at have to exist in the repo, since they are committed
        // and copied into the package as-is.
        var repoRoot = Directory.GetParent(Path.GetDirectoryName(ManifestPath())!)!.Parent!.FullName;
        foreach (var bundle in new[] { manifest.JsBundle!, manifest.CssBundle! })
            Assert.True(File.Exists(Path.Combine(repoRoot, "src", "EmpPrep", bundle.Replace('/', Path.DirectorySeparatorChar))), bundle);
    }

    [Fact]
    public void Manifest_IsALoadableExtensionKind()
    {
        // A non-"extension" kind is registered as metadata only and never loads a DLL.
        var manifest = Parse();
        Assert.Equal("extension", manifest.Kind);
    }

    [Fact]
    public void Manifest_ExternalDependenciesCarryTheRequiredIdentifiers()
    {
        // ExtensionExternalDependency.Id and .Name are both `required`. Omitting either makes
        // System.Text.Json throw for the whole manifest, not just that entry.
        var manifest = Parse();

        Assert.NotEmpty(manifest.ExternalDependencies);
        Assert.All(manifest.ExternalDependencies, dependency =>
        {
            Assert.False(string.IsNullOrWhiteSpace(dependency.Id));
            Assert.False(string.IsNullOrWhiteSpace(dependency.Name));
        });

        var ffmpeg = manifest.ExternalDependencies.Single(dependency => dependency.Id == "ffmpeg");
        Assert.True(ffmpeg.Required);

        var ffprobe = manifest.ExternalDependencies.Single(dependency => dependency.Id == "ffprobe");
        Assert.False(ffprobe.Required);
    }

    [Theory]
    [InlineData("1.4.1")]
    [InlineData("1.4.2-dev.44")]
    [InlineData("1.4.2-dev.102")]
    [InlineData("1.4.2")]
    [InlineData("1.5.0")]
    public void MinCoveVersion_IsSatisfiedByEveryHostWeTarget(string hostVersion)
    {
        // The host refuses to load an extension whose minCoveVersion exceeds the running version,
        // using semver rules. The trap this guards: a prerelease sorts BELOW the release of the same
        // number, so declaring "1.4.2" would be refused by a 1.4.2-dev.N host — which is exactly
        // what a local dev build of Cove reports. A plain earlier release avoids that entirely.
        var manifest = Parse();
        Assert.True(
            CoveVersionCompatibility.IsAtLeast(hostVersion, manifest.MinCoveVersion!),
            $"Host {hostVersion} would refuse to load an extension requiring >={manifest.MinCoveVersion}.");
    }

    [Fact]
    public void MinCoveVersion_ParsesUnderTheHostsSemverRules()
    {
        var manifest = Parse();
        Assert.False(string.IsNullOrWhiteSpace(manifest.MinCoveVersion));
        Assert.True(CoveVersionCompatibility.TryParse(manifest.MinCoveVersion, out _), manifest.MinCoveVersion);
    }
}
