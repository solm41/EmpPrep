using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>The fields prepared for the EMP upload form: title, quality and studio tags, description.</summary>
public sealed class EmpPostTests
{
    private static readonly EmpPrepSettings Defaults = new EmpPrepSettings().Normalized();

    private static PrepMetadata Scene(
        string? studio = "Pornhub",
        string[]? performers = null,
        DateOnly? date = null,
        int width = 1920,
        int height = 1080) => new()
    {
        Title = "Robotic Oil Massage",
        Studio = studio,
        Performers = performers ?? ["Spyder445"],
        Date = date,
        MediaInfo = new PrepMediaInfo { Width = width, Height = height, VideoCodec = "h264" },
    };

    [Theory]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(3840, 2160, "2160p")]
    [InlineData(854, 480, "480p")]
    [InlineData(1080, 1920, "1080p")]   // vertical phone video: the short side decides, not the height
    [InlineData(1440, 1080, "1080p")]
    [InlineData(0, 0, "")]
    public void QualityLabel_UsesTheShorterSide(int width, int height, string expected)
        => Assert.Equal(expected, MetadataBuilder.QualityLabel(width, height));

    [Theory]
    [InlineData("https://www.pornhub.com/channels/x", "pornhub.com")]
    [InlineData("http://brazzers.com", "brazzers.com")]
    [InlineData("tushy.com", "tushy.com")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void WebsiteDomain_IsTheBareHost(string? url, string? expected)
        => Assert.Equal(expected, MetadataBuilder.WebsiteDomain(url));

    [Fact]
    public void WebsiteDomain_FormatsAsASingleTag()
        // The domain's own dot is kept, so it lands as one tag rather than being split.
        => Assert.Equal("pornhub.com", EmpTagFormatter.FormatTag(MetadataBuilder.WebsiteDomain("https://www.pornhub.com")));

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "A" }, "A")]
    [InlineData(new[] { "A", "B" }, "A & B")]
    [InlineData(new[] { "A", "B", "C" }, "A, B & C")]
    public void JoinPerformers_ReadsLikeATitle(string[] performers, string expected)
        => Assert.Equal(expected, MetadataBuilder.JoinPerformers(performers));

    [Fact]
    public void PostTitle_FollowsTheConventionalEmpFormat()
        => Assert.Equal(
            "[Pornhub] Spyder445 - Robotic Oil Massage (2024-05-01) [1080p]",
            MetadataBuilder.RenderPostTitle(Scene(date: new DateOnly(2024, 5, 1)), Defaults));

    [Fact]
    public void PostTitle_DropsEmptyBracketsWhenValuesAreMissing()
    {
        // No studio and no date: no "[]" or "()" left in the title.
        var title = MetadataBuilder.RenderPostTitle(Scene(studio: null), Defaults);
        Assert.Equal("Spyder445 - Robotic Oil Massage [1080p]", title);
    }

    [Fact]
    public void PostTitle_WithNoPerformersHasNoLeadingDash()
    {
        var title = MetadataBuilder.RenderPostTitle(Scene(performers: []), Defaults);
        Assert.Equal("[Pornhub] Robotic Oil Massage [1080p]", title);
    }

    [Fact]
    public void PostTitle_JoinsSeveralPerformers()
        => Assert.StartsWith(
            "[Pornhub] A, B & C - ",
            MetadataBuilder.RenderPostTitle(Scene(performers: ["A", "B", "C"]), Defaults));

    [Fact]
    public void DefaultDescription_UsesEmpsSectionBlocksInsideAQuote()
    {
        // EMP's upload form starts the description with [quote][info][details][screens][/quote].
        var template = Defaults.DescriptionTemplate;

        Assert.Contains("[quote]", template);
        Assert.Contains("[/quote]", template);
        var info = template.IndexOf("[info]", StringComparison.Ordinal);
        var details = template.IndexOf("[details]", StringComparison.Ordinal);
        var screens = template.IndexOf("[screens]", StringComparison.Ordinal);
        Assert.True(info >= 0 && info < details && details < screens, template);

        // The contact sheet belongs under [screens]; the media info under [details].
        Assert.True(template.IndexOf("{screenshots}", StringComparison.Ordinal) > screens);
        Assert.True(template.IndexOf("{mediainfo}", StringComparison.Ordinal) > details);
    }

    [Fact]
    public void DefaultDescription_RendersWithNoPlaceholdersLeft()
    {
        var description = MetadataBuilder.RenderDescription(
            Scene(date: new DateOnly(2024, 5, 1)),
            [new PrepScreenshot(0, "/p/sheet.jpg") { Kind = PrepImageKind.ContactSheet, Url = "https://h/sheet.jpg" }],
            ["tag"],
            Defaults);

        Assert.DoesNotContain("{", description);
        Assert.Contains("[screens]\n[img]https://h/sheet.jpg[/img]", description.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Defaults_AddTheQualityAndStudioWebsiteTagsButNotDatesOrCodec()
    {
        Assert.True(Defaults.IncludeQualityTag);
        Assert.True(Defaults.IncludeStudioWebsiteTag);
        Assert.False(Defaults.IncludeDateTags);
        Assert.False(Defaults.IncludeCodecTag);
    }

    // Exactly what a user's stored settings held after saving under the previous build.
    // Stored with Windows line endings, as the settings form submits them.
    private const string StoredPreviousDefault =
        "[size=4][b]{title}[/b][/size]\r\n\r\n{featured}\r\n\r\n[b]Studio:[/b] {studio}\r\n[b]Performers:[/b] {performers}\r\n"
        + "[b]Date:[/b] {date}\r\n[b]Runtime:[/b] {duration}\r\n\r\n{details}\r\n\r\n[b]Media info[/b]\r\n"
        + "[quote]{mediainfo}[/quote]\r\n\r\n[b]Screens[/b]\r\n{screenshots}";

    [Fact]
    public void StoredCopyOfAnEarlierDefaultTemplate_IsUpgradedToTheCurrentOne()
        => Assert.Equal(
            EmpPrepSettings.DefaultDescriptionTemplate,
            new EmpPrepSettings { DescriptionTemplate = StoredPreviousDefault }.Normalized().DescriptionTemplate);

    [Fact]
    public void AnEditedTemplate_IsNeverReplaced()
    {
        var edited = StoredPreviousDefault.Replace("[b]Screens[/b]", "[b]My screens[/b]");
        Assert.Equal(edited, new EmpPrepSettings { DescriptionTemplate = edited }.Normalized().DescriptionTemplate);
    }

    [Fact]
    public void BlankPostTitleTemplate_FallsBackToTheDefault()
        => Assert.Equal(
            new EmpPrepSettings().PostTitleTemplate,
            new EmpPrepSettings { PostTitleTemplate = "  " }.Normalized().PostTitleTemplate);
}
