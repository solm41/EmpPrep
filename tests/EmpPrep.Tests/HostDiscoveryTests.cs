using Cove.Plugins;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Runs Cove's own <see cref="ExtensionManager.DiscoverExtensions"/> against a staged copy of the
/// build output.
/// </summary>
/// <remarks>
/// This is the test that actually answers "does it load". The unit tests around the manifest and the
/// entry type check each gate in isolation; this drives the host's real discovery code over a real
/// staged directory, including manifest deserialization, the load-context setup and
/// <c>Activator.CreateInstance</c>. An extension that would be silently skipped by Cove is skipped
/// here too, and the assertion fails.
///
/// It does not cover what only a live host can: applying migrations, building the DI container, and
/// serving the endpoints.
/// </remarks>
public sealed class HostDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"empprep-discovery-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Stage the build output the way scripts/stage-local-extension.ps1 does.</summary>
    private string StageExtension()
    {
        var output = AppContext.BaseDirectory;

        // The test binaries sit beside the extension's own output via the project reference, so
        // EmpPrep.dll and the copied extension.json are both here.
        var manifest = Path.Combine(output, "extension.json");
        Assert.True(
            File.Exists(manifest),
            $"extension.json was not copied to the build output ({output}). Without it the host has no manifest to read.");

        var staged = Path.Combine(_root, "cove.community.empprep");
        Directory.CreateDirectory(staged);

        File.Copy(manifest, Path.Combine(staged, "extension.json"));
        File.Copy(Path.Combine(output, "EmpPrep.dll"), Path.Combine(staged, "EmpPrep.dll"));

        var dist = Path.Combine(output, "dist");
        if (Directory.Exists(dist))
        {
            var stagedDist = Path.Combine(staged, "dist");
            Directory.CreateDirectory(stagedDist);
            foreach (var file in Directory.GetFiles(dist))
                File.Copy(file, Path.Combine(stagedDist, Path.GetFileName(file)));
        }

        return staged;
    }

    private ExtensionManager CreateManager(string coveVersion) => new(new ExtensionContext
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.Combine(_root, "data"),
        CoveVersion = coveVersion,
    });

    [Fact]
    public void HostDiscovery_FindsAndInstantiatesTheExtension()
    {
        StageExtension();
        var manager = CreateManager("1.4.2-dev.102");

        manager.DiscoverExtensions(_root);

        var extension = manager.GetExtension("cove.community.empprep");
        Assert.NotNull(extension);
        Assert.Equal("EMP Prep", extension!.Name);
        Assert.Equal("0.1.0", extension.Version);
    }

    [Fact]
    public void HostDiscovery_RegistersItAsALoadableExtensionWithItsUiContributions()
    {
        StageExtension();
        var manager = CreateManager("1.4.2-dev.102");

        manager.DiscoverExtensions(_root);

        var extension = Assert.IsAssignableFrom<IUIExtension>(manager.GetExtension("cove.community.empprep"));
        var ui = extension.GetUIManifest();

        // What makes the feature reachable in the UI at all: the nav page and the two video actions.
        Assert.Single(ui.Pages);
        Assert.Equal(2, ui.Actions.Count);
    }

    [Fact]
    public void HostDiscovery_ReportsNoDependencyProblems()
    {
        StageExtension();
        var manager = CreateManager("1.4.2-dev.102");
        manager.DiscoverExtensions(_root);

        // Covers the minCoveVersion gate and the (empty) extension dependency set. A failure here is
        // what produces "Requires Cove >=x but running y" instead of a working extension.
        var problems = manager.ValidateDependencies()
            .Where(problem => problem.ExtensionId == "cove.community.empprep")
            .ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void HostDiscovery_IsNotSilentlySkippedByAMalformedManifest()
    {
        // The regression this guards. An earlier manifest declared externalDependencies without the
        // `id` that ExtensionExternalDependency marks `required`; System.Text.Json threw for the
        // whole file and the host skipped the directory, so the extension never appeared in Cove
        // and nothing in the UI said why. Corrupting the manifest must fail discovery here.
        var staged = StageExtension();
        File.WriteAllText(Path.Combine(staged, "extension.json"), """
            {
              "id": "cove.community.empprep",
              "name": "EMP Prep",
              "version": "0.1.0",
              "entryDll": "EmpPrep.dll",
              "externalDependencies": [{ "name": "FFmpeg" }]
            }
            """);

        var manager = CreateManager("1.4.2-dev.102");
        manager.DiscoverExtensions(_root);

        Assert.Null(manager.GetExtension("cove.community.empprep"));
    }
}
