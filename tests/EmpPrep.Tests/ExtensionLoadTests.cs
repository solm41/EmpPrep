using Cove.Plugins;
using Cove.Sdk;

using EmpPrep;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Reproduces what the host's discovery does after it has parsed the manifest.
/// </summary>
/// <remarks>
/// <see cref="ManifestTests"/> covers the manifest half of loading. This covers the other half: the
/// entry DLL has to expose a concrete <see cref="IExtension"/> that <c>Activator.CreateInstance</c>
/// can build with no arguments, and it has to surface its identity from the manifest once the host
/// calls <c>ApplyManifest</c>. Between them, an extension that would silently fail to appear in Cove
/// fails here instead.
/// </remarks>
public sealed class ExtensionLoadTests
{
    [Fact]
    public void EntryAssembly_ExposesExactlyOneConcreteExtensionType()
    {
        // Discovery scans the entry DLL for every non-abstract IExtension implementation. More than
        // one would register twice under whichever ids they report.
        var types = typeof(EmpPrepExtension).Assembly.GetTypes()
            .Where(type => typeof(IExtension).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            .ToList();

        Assert.Single(types);
        Assert.Equal(typeof(EmpPrepExtension), types[0]);
    }

    [Fact]
    public void ExtensionType_IsConstructibleTheWayTheHostConstructsIt()
    {
        // Activator.CreateInstance with no arguments. A constructor taking dependencies — or a
        // static initializer that throws — makes the type unloadable.
        var instance = Activator.CreateInstance(typeof(EmpPrepExtension));

        Assert.NotNull(instance);
        Assert.IsAssignableFrom<IExtension>(instance);
    }

    [Fact]
    public void Extension_SurfacesItsIdentityFromTheManifest()
    {
        // CoveExtensionBase reads Id/Name/Version off the manifest rather than from C#, so reading
        // them before ApplyManifest throws. The host always applies it first; this asserts the
        // extension actually cooperates with that contract.
        var extension = new EmpPrepExtension();
        var manifest = new ExtensionManifestFile
        {
            Id = "cove.community.empprep",
            Name = "EMP Prep",
            Version = "0.1.0",
        };

        Assert.IsAssignableFrom<IManifestAware>(extension);
        ((IManifestAware)extension).ApplyManifest(manifest);

        Assert.Equal("cove.community.empprep", extension.Id);
        Assert.Equal("EMP Prep", extension.Name);
        Assert.Equal("0.1.0", extension.Version);
    }

    [Fact]
    public void Extension_ImplementsTheCapabilitiesItsManifestAndUiImply()
    {
        var extension = new EmpPrepExtension();

        // Tables in Cove's database, HTTP endpoints, a nav page and actions, a settings store, and
        // background jobs. Losing any of these interfaces silently drops that capability.
        Assert.IsAssignableFrom<IDataExtension>(extension);
        Assert.IsAssignableFrom<IApiExtension>(extension);
        Assert.IsAssignableFrom<IUIExtension>(extension);
        Assert.IsAssignableFrom<IStatefulExtension>(extension);
    }

    [Fact]
    public void UiManifest_RegistersThePageActionsAndSettingsTheUiNeeds()
    {
        var extension = new EmpPrepExtension();
        ((IManifestAware)extension).ApplyManifest(new ExtensionManifestFile
        {
            Id = "cove.community.empprep",
            Name = "EMP Prep",
            Version = "0.1.0",
        });

        var ui = extension.GetUIManifest();

        // The nav page, and the component name the JS bundle must export under.
        var page = Assert.Single(ui.Pages);
        Assert.Equal("emp-prep", page.Route);
        Assert.Equal("EmpPrepPage", page.ComponentName);
        Assert.True(page.ShowInNav);

        // Two actions on videos: the multi-select toolbar ("bulk") and the detail page ("toolbar").
        // The host filters strictly on actionType, so a wrong value means the button never renders.
        Assert.Equal(2, ui.Actions.Count);
        Assert.Contains(ui.Actions, action => action.ActionType == "bulk");
        Assert.Contains(ui.Actions, action => action.ActionType == "toolbar");
        Assert.All(ui.Actions, action =>
        {
            Assert.Equal(["video"], action.EntityTypes);
            // The host normalizes this against its own /api base, so it must be the full path.
            Assert.Equal("/api/ext/empprep/add", action.ApiEndpoint);
            Assert.Equal("cove.community.empprep", action.ExtensionId);
        });

        var panel = Assert.Single(ui.SettingsPanels);
        Assert.Equal("EmpPrepSettingsPanel", panel.ComponentName);
        Assert.Single(ui.SettingsTabs);
    }

    [Fact]
    public void Migrations_AreDeclaredAndRepeatable()
    {
        var extension = new EmpPrepExtension();

        // GetMigrations rebuilds the list on each call; a DefineMigrations that appended to shared
        // state would grow it and reapply scripts.
        var first = ((IDataExtension)extension).GetMigrations();
        var second = ((IDataExtension)extension).GetMigrations();

        Assert.Equal(4, first.Count);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal("001_empprep", first[0].Name);
        Assert.Contains("empprep_items", first[0].UpSql);

        // Added columns must be idempotent: the host may retry a migration whose receipt did not commit.
        Assert.Equal("002_empprep_piece_info", first[1].Name);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"TorrentPieceLength\"", first[1].UpSql);
        Assert.Equal("003_empprep_post_title", first[2].Name);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"PostTitleOverride\"", first[2].UpSql);
        Assert.Equal("004_empprep_packs", first[3].Name);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"PackFolder\"", first[3].UpSql);
    }
}
