using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// An edited description must keep following the theme field and the screens. Before, a theme typed
/// at the top or screens uploaded later never reached an edited description until it was reset.
/// </summary>
public sealed class LiveDescriptionTests
{
    private static readonly EmpPrepSettings Settings = new EmpPrepSettings().Normalized();

    private static readonly PrepMetadata Derived = new()
    {
        Title = "Robotic Oil Massage",
        Studio = "Pornhub",
        Performers = ["Spyder445"],
        FormattedTags = ["spyder445", "massage"],
        MediaInfo = new PrepMediaInfo { Width = 1920, Height = 1080 },
    };

    private static readonly IReadOnlyList<PrepScreenshot> NotUploaded =
    [
        new PrepScreenshot(0, "/p/00_cover.jpg") { Kind = PrepImageKind.Cover, Featured = true },
        new PrepScreenshot(0, "/p/00_contact_sheet.jpg") { Kind = PrepImageKind.ContactSheet },
    ];

    private static readonly IReadOnlyList<PrepScreenshot> Uploaded =
    [
        NotUploaded[0] with { Url = "https://h/cover.jpg" },
        NotUploaded[1] with { Url = "https://h/sheet.jpg", ThumbUrl = "https://h/sheet.th.jpg" },
    ];

    private static string Shown(IReadOnlyList<PrepScreenshot> shots)
        => MetadataBuilder.RenderDescription(Derived with { PostTitle = "t" }, shots, Derived.FormattedTags, Settings);

    [Fact]
    public void AnEditedVideoDescription_PicksUpScreensUploadedAfterTheEdit()
    {
        var edited = Shown(NotUploaded).Replace("Studio: Pornhub", "Studio: Pornhub — my note");
        var stored = EmpPrepService.StoredEdits(Derived, NotUploaded, new ItemOverrides(null, null, edited, null), Settings).Description!;

        var afterUpload = LiveDescription.Fill(stored, MetadataBuilder.DescriptionSlots(Uploaded));

        Assert.Contains("my note", afterUpload);
        Assert.Contains("[img]https://h/cover.jpg[/img]", afterUpload);
        Assert.Contains("[img]https://h/sheet.jpg[/img]", afterUpload);
        Assert.DoesNotContain("NOT UPLOADED", afterUpload);
    }

    [Fact]
    public void AnEditSavedBeforeMarkersExisted_StillPicksUpUploads()
    {
        var legacy = Shown(NotUploaded) + "\n\nmy note";
        var shown = LiveDescription.Fill(legacy, MetadataBuilder.DescriptionSlots(Uploaded));

        Assert.EndsWith("my note", shown);
        Assert.DoesNotContain("NOT UPLOADED", shown);
    }

    [Fact]
    public void AnUnchangedDescription_ReadsBackExactly()
    {
        var text = Shown(Uploaded);
        var slots = MetadataBuilder.DescriptionSlots(Uploaded);
        Assert.Equal(text, LiveDescription.Fill(LiveDescription.Templatize(text, slots), slots));
    }

    private static List<PackScene> Scenes(bool hosted) =>
    [
        PackFixtures.Scene("One", ["Spyder445"], hosted: hosted),
        PackFixtures.Scene("Two", ["Spyder445"], hosted: hosted),
    ];

    private static string PackShown(List<PackScene> scenes, string theme)
    {
        var stats = PackComposer.Stats(scenes);
        return PackComposer.Description(scenes, stats, PackComposer.Title(stats, Settings), theme, null, Settings);
    }

    [Fact]
    public void ATypedTheme_ReachesAnEditedPackDescription()
    {
        var scenes = Scenes(hosted: false);
        // Edited while the theme was still empty, then saved together with a new theme.
        var edited = PackShown(scenes, "") + "\n\nThanks for downloading!";
        var stored = LiveDescription.Templatize(edited, PackComposer.DescriptionSlots(scenes, "Oiled massages", null, Settings, previousTheme: null));

        var shown = LiveDescription.Fill(stored, PackComposer.DescriptionSlots(scenes, "Oiled massages", null, Settings));

        Assert.Contains("\nOiled massages\n", shown);
        Assert.DoesNotContain("Describe what this pack", shown);
        Assert.EndsWith("Thanks for downloading!", shown);
    }

    [Fact]
    public void AChangedTheme_ReplacesTheOldOneInAnEditedPackDescription()
    {
        var scenes = Scenes(hosted: false);
        var edited = PackShown(scenes, "Old theme") + "\n\nextra";
        var stored = LiveDescription.Templatize(edited, PackComposer.DescriptionSlots(scenes, "New theme", null, Settings, previousTheme: "Old theme"));

        var shown = LiveDescription.Fill(stored, PackComposer.DescriptionSlots(scenes, "New theme", null, Settings));

        Assert.Contains("New theme", shown);
        Assert.DoesNotContain("Old theme", shown);
    }

    [Fact]
    public void AnEditedPackDescription_PicksUpUploadedScreens()
    {
        var edited = PackShown(Scenes(hosted: false), "Theme") + "\n\nextra";
        var hosted = Scenes(hosted: true);

        var shown = LiveDescription.Fill(edited, PackComposer.DescriptionSlots(hosted, "Theme", null, Settings));

        Assert.Contains("[url=https://hamsterimg.net/i/One.jpg]", shown);
        Assert.DoesNotContain("NOT UPLOADED", shown);
    }

    [Fact]
    public void AShortTheme_IsMatchedOnItsOwnLineRatherThanInsideTheTitle()
    {
        var slots = new[] { new LiveSlot("theme", "New", ["Spyder445"]) };
        var text = "[b]Spyder445 - Megapack[/b]\n\nSpyder445\n\n[details]";

        Assert.Equal("[b]Spyder445 - Megapack[/b]\n\nNew\n\n[details]", LiveDescription.Fill(text, slots));
    }

    [Fact]
    public void StoredOldDefaultTemplates_AreUpgradedToTheQuoteFreeScreens()
    {
        var stored = new EmpPrepSettings
        {
            DescriptionTemplate = EmpPrepSettings.DefaultDescriptionTemplate.Replace("\r\n", "\n").Replace("{mediainfo}\n[/quote]", "{mediainfo}")
                .Replace("{screenshots}", "{screenshots}\n[/quote]"),
            PackDescriptionTemplate = EmpPrepSettings.DefaultPackDescriptionTemplate.Replace("\r\n", "\n").Replace("{tags}\n[/quote]", "{tags}")
                .Replace("{screens}", "{screens}\n[/quote]"),
        }.Normalized();

        Assert.Equal(EmpPrepSettings.DefaultDescriptionTemplate, stored.DescriptionTemplate);
        Assert.Equal(EmpPrepSettings.DefaultPackDescriptionTemplate, stored.PackDescriptionTemplate);
    }

    [Theory]
    [InlineData(EmpPrepSettings.DefaultDescriptionTemplate)]
    [InlineData(EmpPrepSettings.DefaultPackDescriptionTemplate)]
    public void DefaultTemplates_CloseTheQuoteBeforeTheScreens(string template)
        => Assert.True(template.IndexOf("[/quote]", StringComparison.Ordinal) < template.IndexOf("[screens]", StringComparison.Ordinal));
}
