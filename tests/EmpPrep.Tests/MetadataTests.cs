using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

public sealed class EmpTagFormatterTests
{
    [Theory]
    [InlineData("Big Tits", "big.tits")]
    [InlineData("Riley Reid", "riley.reid")]
    [InlineData("girl/girl", "girl.girl")]
    [InlineData("  Anal   Sex  ", "anal.sex")]
    [InlineData("MILF", "milf")]
    [InlineData("3some", "3some")]
    public void FormatTag_JoinsWordsWithDots(string input, string expected)
        => Assert.Equal(expected, EmpTagFormatter.FormatTag(input));

    [Fact]
    public void FormatTag_CollapsesRunsOfPunctuationIntoOneSeparator()
    {
        // Two spaces, a hyphen and an existing dot must not produce "big..tits" — a tracker treats
        // that as a different tag from "big.tits".
        Assert.Equal("big.tits", EmpTagFormatter.FormatTag("Big  Tits"));
        Assert.Equal("big.tits", EmpTagFormatter.FormatTag("Big - Tits"));
        Assert.Equal("big.tits", EmpTagFormatter.FormatTag("Big.Tits"));
    }

    [Fact]
    public void FormatTag_StripsAccentsSoTheTagMatchesItsPlainSpelling()
        => Assert.Equal("anais.beau", EmpTagFormatter.FormatTag("Anaïs Beau"));

    [Fact]
    public void FormatTag_TrimsLeadingAndTrailingSeparators()
    {
        Assert.Equal("solo", EmpTagFormatter.FormatTag("  -solo-  "));
        Assert.Equal("solo", EmpTagFormatter.FormatTag("(solo)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData(null)]
    public void FormatTag_ReturnsNullWhenNothingSurvives(string? input)
        => Assert.Null(EmpTagFormatter.FormatTag(input));

    [Fact]
    public void FormatTag_CanPreserveCase()
        => Assert.Equal("Big.Tits", EmpTagFormatter.FormatTag("Big Tits", lowercase: false));

    [Fact]
    public void FormatTags_DeduplicatesWhileKeepingOrder()
    {
        var tags = EmpTagFormatter.FormatTags(["Big Tits", "Anal", "big tits", "Big.Tits"]);
        Assert.Equal(["big.tits", "anal"], tags);
    }

    [Fact]
    public void FormatTags_AppliesExclusionsAfterDotting()
    {
        // The exclusion list is user-typed, so "To Sort" must exclude the tag that dots to "to.sort".
        var tags = EmpTagFormatter.FormatTags(["Big Tits", "To Sort", "Anal"], excluded: ["To Sort"]);
        Assert.Equal(["big.tits", "anal"], tags);
    }

    [Fact]
    public void Join_SeparatesTagsWithSpaces()
    {
        // The distinction that matters: dots go inside a tag, spaces between tags. Getting this
        // backwards would submit every tag as one long token.
        Assert.Equal("big.tits anal riley.reid", EmpTagFormatter.Join(["big.tits", "anal", "riley.reid"]));
    }

    [Fact]
    public void ParseUserTags_AcceptsSpacesCommasAndNewlines()
    {
        var tags = EmpTagFormatter.ParseUserTags("big.tits, anal\nriley reid");
        Assert.Equal(["big.tits", "anal", "riley", "reid"], tags);
    }
}

public sealed class ScreenshotPlanningTests
{
    private static readonly EmpPrepSettings Defaults = new EmpPrepSettings().Normalized();

    [Fact]
    public void ChooseTimestamps_GivesAtLeastTwelveFramesForAShortFeature()
    {
        // 10 minutes at 90s spacing is only 7 frames, so the minimum count must take over.
        var timestamps = ScreenshotService.ChooseTimestamps(600, Defaults);
        Assert.Equal(12, timestamps.Count);
    }

    [Fact]
    public void ChooseTimestamps_SpacesRoughlyOnePerNinetySecondsForLongerVideos()
    {
        // 45 minutes: spacing wins over the floor, and lands in the "one every one to two minutes"
        // band the settings describe.
        var timestamps = ScreenshotService.ChooseTimestamps(45 * 60, Defaults);
        Assert.InRange(timestamps.Count, 28, 32);

        var gaps = timestamps.Zip(timestamps.Skip(1), (first, second) => second - first).ToList();
        Assert.All(gaps, gap => Assert.InRange(gap, 60, 120));
    }

    [Fact]
    public void ChooseTimestamps_ReducesTheCountForAVeryShortClip()
    {
        // 30 seconds cannot hold 12 frames 4s apart, so the floor yields — this is the "unless it
        // is really short" case, and it is what stops a clip producing a dozen near-identical stills.
        var timestamps = ScreenshotService.ChooseTimestamps(30, Defaults);
        Assert.InRange(timestamps.Count, 1, 8);

        var gaps = timestamps.Zip(timestamps.Skip(1), (first, second) => second - first);
        Assert.All(gaps, gap => Assert.True(gap >= Defaults.MinFrameSpacingSeconds, $"gap was {gap}"));
    }

    [Fact]
    public void ChooseTimestamps_HonoursTheMaximum()
    {
        // Four hours would otherwise be 160 frames.
        var timestamps = ScreenshotService.ChooseTimestamps(4 * 60 * 60, Defaults);
        Assert.Equal(Defaults.MaxFrameCount, timestamps.Count);
    }

    [Fact]
    public void ChooseTimestamps_SkipsTheIntroAndOutroMargins()
    {
        var duration = 60 * 60d;
        var timestamps = ScreenshotService.ChooseTimestamps(duration, Defaults);

        // Nothing in the first or last 2% — that is where intros and credits live.
        Assert.All(timestamps, seconds => Assert.InRange(
            seconds,
            duration * Defaults.SkipStartPercent / 100,
            duration * (1 - Defaults.SkipEndPercent / 100)));
    }

    [Fact]
    public void ChooseTimestamps_IsSortedAndDistinct()
    {
        var timestamps = ScreenshotService.ChooseTimestamps(30 * 60, Defaults);
        Assert.Equal(timestamps.OrderBy(value => value).ToList(), timestamps);
        Assert.Equal(timestamps.Distinct().Count(), timestamps.Count);
    }

    [Fact]
    public void ChooseTimestamps_ReturnsNothingWithoutADuration()
    {
        // A video the scanner never probed has duration 0; planning must not invent a frame at 0s.
        Assert.Empty(ScreenshotService.ChooseTimestamps(0, Defaults));
        Assert.Empty(ScreenshotService.ChooseTimestamps(-1, Defaults));
    }

    [Fact]
    public void ChooseTimestamps_FallsBackToTheWholeClipWhenMarginsWouldConsumeIt()
    {
        var aggressive = (new EmpPrepSettings { SkipStartPercent = 40, SkipEndPercent = 40 }).Normalized();
        var timestamps = ScreenshotService.ChooseTimestamps(120, aggressive);
        Assert.NotEmpty(timestamps);
        Assert.All(timestamps, seconds => Assert.InRange(seconds, 0, 120));
    }
}

public sealed class DescriptionRenderingTests
{
    private static readonly EmpPrepSettings Defaults = new EmpPrepSettings().Normalized();

    private static PrepMetadata SampleMetadata() => new()
    {
        VideoId = 7,
        Title = "A Scene",
        Studio = "Example Studio",
        Performers = ["Riley Reid", "Anaïs Beau"],
        Details = "Some description text.",
        Date = new DateOnly(2024, 5, 1),
        MediaInfo = new PrepMediaInfo
        {
            FileName = "scene.mp4",
            FilePath = "/media/scene.mp4",
            FileSizeBytes = 2_684_354_560,
            Container = "mp4",
            Width = 1920,
            Height = 1080,
            DurationSeconds = 1830,
            VideoCodec = "h264",
            AudioCodec = "aac",
            FrameRate = 29.97,
            BitRateBps = 11_500_000,
            AudioChannels = 2,
            AudioSampleRate = 48_000,
        },
    };

    [Fact]
    public void RenderContactSheet_MarksASheetThatIsNotHostedYet()
    {
        // A bare [img] tag with a local path would render as a broken image on the tracker. The
        // marker makes an incomplete upload obvious before it is submitted.
        var sheet = MetadataBuilder.RenderContactSheet(
            [new PrepScreenshot(0, "/prep/00_contact_sheet.jpg") { Kind = PrepImageKind.ContactSheet }]);

        Assert.Equal("[NOT UPLOADED: 00_contact_sheet.jpg]", sheet);
    }

    [Fact]
    public void RenderContactSheet_SaysSoWhenThereIsNoSheet()
        => Assert.Contains("No screenshots", MetadataBuilder.RenderContactSheet([]));

    [Fact]
    public void RenderDescription_SubstitutesEveryDocumentedPlaceholder()
    {
        var metadata = SampleMetadata();
        var description = MetadataBuilder.RenderDescription(
            metadata,
            [new PrepScreenshot(0, "/prep/00_contact_sheet.jpg") { Kind = PrepImageKind.ContactSheet, Url = "https://host/1.jpg" }],
            ["big.tits", "anal"],
            Defaults);

        Assert.Contains("A Scene", description);
        Assert.Contains("Example Studio", description);
        Assert.Contains("Riley Reid, Anaïs Beau", description);
        Assert.Contains("2024-05-01", description);
        Assert.Contains("Some description text.", description);
        Assert.Contains("https://host/1.jpg", description);
        Assert.Contains("h264", description);

        // No placeholder should survive unrendered into the posted description.
        Assert.DoesNotContain("{", description);
    }

    [Fact]
    public void RenderMediaInfoText_ReportsSizeDurationAndCodecs()
    {
        var text = MetadataBuilder.RenderMediaInfoText(SampleMetadata().MediaInfo);

        Assert.Contains("scene.mp4", text);
        Assert.Contains("2.5 GiB", text);
        Assert.Contains("1920x1080", text);
        Assert.Contains("30m 30s", text);
        Assert.Contains("29.97 fps", text);
        Assert.Contains("11.5 Mb/s", text);
        Assert.Contains("2 ch", text);
        Assert.Contains("48000 Hz", text);
    }

    [Fact]
    public void RenderMediaInfoText_SaysUnknownRatherThanBlankForMissingFields()
    {
        var text = MetadataBuilder.RenderMediaInfoText(new PrepMediaInfo { FileName = "x.mkv" });
        Assert.Contains("unknown", text);
    }

    [Fact]
    public void RenderReleaseName_FillsTheTemplateAndStaysFilesystemSafe()
    {
        var name = MetadataBuilder.RenderReleaseName(SampleMetadata(), Defaults);

        Assert.Contains("Example Studio", name);
        Assert.Contains("A Scene", name);
        Assert.Contains("1080p", name);
        Assert.All(Path.GetInvalidFileNameChars(), invalid => Assert.DoesNotContain(invalid, name));
    }

    [Fact]
    public void RenderReleaseName_DoesNotLeaveDanglingSeparatorsWhenFieldsAreMissing()
    {
        // A video with no studio and no performers must not yield a name starting with " - - ".
        var sparse = SampleMetadata() with { Studio = null, Performers = [] };
        var name = MetadataBuilder.RenderReleaseName(sparse, Defaults);

        Assert.False(name.StartsWith('-'), name);
        Assert.False(name.EndsWith('-'), name);
        Assert.DoesNotContain("- -", name);
        Assert.Contains("A Scene", name);
    }
}

public sealed class SpriteReaderTests
{
    [Fact]
    public void ParseVtt_ReadsTimingsAndTileRectangles()
    {
        // The exact shape Cove's thumbnail service writes.
        const string vtt = """
            WEBVTT

            00:00:00.000 --> 00:00:30.000
            42_sprite.jpg#xywh=0,0,160,90

            00:00:30.000 --> 00:01:00.000
            42_sprite.jpg#xywh=160,0,160,90

            00:01:00.000 --> 00:01:30.000
            42_sprite.jpg#xywh=0,90,160,90
            """;

        var tiles = SpriteReader.ParseVtt(vtt);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(0, tiles[0].StartSeconds);
        Assert.Equal(30, tiles[1].StartSeconds);
        Assert.Equal(60, tiles[2].StartSeconds);
        Assert.Equal((160, 0, 160, 90), (tiles[1].X, tiles[1].Y, tiles[1].Width, tiles[1].Height));
    }

    [Fact]
    public void ParseVtt_HandlesHoursAndCommaDecimalSeparators()
    {
        const string vtt = """
            WEBVTT

            01:02:03,500 --> 01:02:33,500
            s.jpg#xywh=0,0,160,90
            """;

        var tiles = SpriteReader.ParseVtt(vtt);
        Assert.Single(tiles);
        Assert.Equal(3723.5, tiles[0].StartSeconds, 3);
    }

    [Fact]
    public void ParseVtt_IgnoresContentWithoutTileGeometry()
    {
        // A plain caption file is not a sprite index; reading it as one would produce bogus
        // timestamps rather than simply falling back to automatic selection.
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:04.000
            Some subtitle text.
            """;

        Assert.Empty(SpriteReader.ParseVtt(vtt));
    }

    [Fact]
    public void ParseVtt_ReturnsEmptyForGarbage()
    {
        Assert.Empty(SpriteReader.ParseVtt(""));
        Assert.Empty(SpriteReader.ParseVtt("not a vtt file at all"));
    }
}

public sealed class HamsterImgResponseTests
{
    // Shape of a real Chevereto v1 upload response, trimmed to the fields that are read.
    private const string Success = """
        {
          "status_code": 200,
          "success": { "message": "image uploaded", "code": 200 },
          "image": {
            "name": "01_00h00m22s",
            "url": "https://hamsterimg.net/images/2026/09/18/01_00h00m22s.jpg",
            "url_viewer": "https://hamsterimg.net/image/AbC12",
            "thumb": { "url": "https://hamsterimg.net/images/2026/09/18/01_00h00m22s.th.jpg" },
            "medium": { "url": "https://hamsterimg.net/images/2026/09/18/01_00h00m22s.md.jpg" }
          },
          "status_txt": "OK"
        }
        """;

    [Fact]
    public void ParseResponse_ReadsFullThumbnailMediumAndViewerLinks()
    {
        var hosted = HamsterImgUploader.ParseResponse(200, Success);

        Assert.Equal("https://hamsterimg.net/images/2026/09/18/01_00h00m22s.jpg", hosted.Url);
        Assert.Equal("https://hamsterimg.net/images/2026/09/18/01_00h00m22s.th.jpg", hosted.ThumbUrl);
        Assert.Equal("https://hamsterimg.net/images/2026/09/18/01_00h00m22s.md.jpg", hosted.MediumUrl);
        Assert.Equal("https://hamsterimg.net/image/AbC12", hosted.ViewerUrl);
    }

    [Fact]
    public void ParseResponse_SurfacesTheHostsOwnErrorMessage()
    {
        // A bad key must say so, rather than a generic "upload failed".
        var exception = Assert.Throws<InvalidOperationException>(() => HamsterImgUploader.ParseResponse(
            400, """{"status_code":400,"error":{"message":"Invalid API v1 key.","code":100},"status_txt":"Bad Request"}"""));

        Assert.Contains("Invalid API v1 key.", exception.Message);
    }

    [Fact]
    public void ParseResponse_TreatsAnErrorStatusInTheBodyAsAFailureEvenOnHttp200()
    {
        // Chevereto reports failures in the body; the transport status alone is not trusted.
        Assert.Throws<InvalidOperationException>(() => HamsterImgUploader.ParseResponse(
            200, """{"status_code":403,"error":{"message":"Forbidden"}}"""));
    }

    [Fact]
    public void ParseResponse_RejectsANonJsonBody()
        => Assert.Throws<InvalidOperationException>(() => HamsterImgUploader.ParseResponse(502, "<html>Bad gateway</html>"));

    [Fact]
    public void ParseResponse_RequiresTheFullSizeUrl()
        => Assert.Throws<InvalidOperationException>(() => HamsterImgUploader.ParseResponse(
            200, """{"status_code":200,"image":{"thumb":{"url":"https://h/t.jpg"}}}"""));

    [Fact]
    public void DescribeUnavailable_AsksForTheApiKeyUntilItIsSet()
    {
        Assert.Contains("API key", HamsterImgUploader.DescribeUnavailable(new EmpPrepSettings())!);
        Assert.Null(HamsterImgUploader.DescribeUnavailable(new EmpPrepSettings { HamsterImgApiKey = "k" }));
    }
}

public sealed class UploadSetTests
{
    private static PrepScreenshot Image(string name, string kind = PrepImageKind.Frame, bool featured = false, bool hosted = true) =>
        new(10, $"/prep/{name}.jpg")
        {
            Kind = kind,
            Featured = featured,
            Url = hosted ? $"https://hamsterimg.net/i/{name}.jpg" : null,
        };

    private static List<PrepScreenshot> TypicalSet() =>
    [
        Image("cover", PrepImageKind.Cover, featured: true),
        Image("sheet", PrepImageKind.ContactSheet),
        Image("f1"),
        Image("f2", featured: true),
        Image("f3"),
    ];

    [Fact]
    public void OnlyFullSizeImagesAndTheContactSheetUpload()
    {
        var uploads = TypicalSet().Where(image => image.Uploads).Select(image => image.FileName).ToList();
        Assert.Equal(["cover.jpg", "sheet.jpg", "f2.jpg"], uploads);
    }

    [Fact]
    public void ALegacyFeaturedContactSheet_IsStillTheScreensSectionNotAFullSizeImage()
    {
        // Earlier builds flagged the sheet as featured. It must not render twice.
        var legacySheet = Image("sheet", PrepImageKind.ContactSheet, featured: true);
        Assert.False(legacySheet.IsFullSize);
        Assert.True(legacySheet.Uploads);
    }

    [Fact]
    public void Description_HasTheFullSizeImagesThenTheSheet_AndNoIndividualFrames()
    {
        var description = MetadataBuilder.RenderDescription(
            new PrepMetadata { Title = "T" }, TypicalSet(), ["tag"], new EmpPrepSettings().Normalized());

        Assert.Contains("[img]https://hamsterimg.net/i/cover.jpg[/img]", description);
        Assert.Contains("[img]https://hamsterimg.net/i/f2.jpg[/img]", description);
        Assert.Contains("[img]https://hamsterimg.net/i/sheet.jpg[/img]", description);

        // Frames that only feed the sheet are never referenced.
        Assert.DoesNotContain("f1.jpg", description);
        Assert.DoesNotContain("f3.jpg", description);

        // Full-size images come before the screens section.
        Assert.True(description.IndexOf("cover.jpg", StringComparison.Ordinal)
            < description.IndexOf("sheet.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void FeaturedImages_RenderEachFullSizeImageOnce()
    {
        var featured = MetadataBuilder.RenderFeaturedImages(TypicalSet());
        Assert.Equal(
            "[img]https://hamsterimg.net/i/cover.jpg[/img]\n\n[img]https://hamsterimg.net/i/f2.jpg[/img]",
            featured);
    }

    [Fact]
    public void AnImageNotYetUploaded_ShowsAMarkerInsteadOfABrokenTag()
        => Assert.Equal(
            "[NOT UPLOADED: cover.jpg]",
            MetadataBuilder.RenderFeaturedImages([Image("cover", PrepImageKind.Cover, featured: true, hosted: false)]));

    [Theory]
    [InlineData(12, 1, new[] { 6 })]       // one frame: the middle
    [InlineData(12, 2, new[] { 4, 8 })]    // two frames: the thirds
    [InlineData(3, 5, new[] { 0, 1, 2 })]  // never more than there are
    [InlineData(12, 0, new int[0])]
    [InlineData(0, 2, new int[0])]
    public void PickSpreadIndexes_SpreadsPicksThroughTheVideo(int total, int count, int[] expected)
        => Assert.Equal(expected, EmpPrep.Services.EmpPrepService.PickSpreadIndexes(total, count));
}

public sealed class SettingsNormalizationTests
{
    [Fact]
    public void Normalized_ClampsValuesOutOfRange()
    {
        var settings = new EmpPrepSettings
        {
            SecondsPerFrame = 0,
            MinFrameCount = 0,
            MaxFrameCount = 100_000,
            JpegQuality = 500,
            BbcodeColumns = 99,
            SkipStartPercent = 90,
            PieceLengthKiB = -5,
            ScreenshotWidth = -10,
        }.Normalized();

        Assert.InRange(settings.SecondsPerFrame, 5, 3600);
        Assert.InRange(settings.MinFrameCount, 1, 200);
        Assert.InRange(settings.MaxFrameCount, 1, 200);
        Assert.InRange(settings.JpegQuality, 1, 100);
        Assert.InRange(settings.BbcodeColumns, 1, 6);
        Assert.InRange(settings.SkipStartPercent, 0, 40);
        Assert.Equal(0, settings.PieceLengthKiB);
        Assert.Equal(0, settings.ScreenshotWidth);
    }

    [Fact]
    public void Normalized_RestoresTheDefaultTemplateWhenBlanked()
        => Assert.Equal(
            EmpPrepSettings.DefaultDescriptionTemplate,
            new EmpPrepSettings { DescriptionTemplate = "   " }.Normalized().DescriptionTemplate);

    [Theory]
    [InlineData("PNG", "png")]
    [InlineData("jpeg", "jpg")]
    [InlineData("", "jpg")]
    public void Normalized_ConstrainsTheScreenshotFormat(string input, string expected)
        => Assert.Equal(expected, new EmpPrepSettings { ScreenshotFormat = input }.Normalized().ScreenshotFormat);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(50, 6)]
    public void Normalized_ClampsTheFullSizeImageCount(int input, int expected)
        => Assert.Equal(expected, new EmpPrepSettings { FullSizeImageCount = input }.Normalized().FullSizeImageCount);

    [Fact]
    public void Defaults_AreTwoFullSizeImagesStartingWithTheCover()
    {
        var settings = new EmpPrepSettings().Normalized();
        Assert.True(settings.IncludeCoverImage);
        Assert.Equal(2, settings.FullSizeImageCount);
        Assert.Contains("{featured}", settings.DescriptionTemplate);
    }
}
