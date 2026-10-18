using System.Text;

using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

internal static class PackFixtures
{
    public static readonly EmpPrepSettings Settings = new EmpPrepSettings().Normalized();

    public static PackScene Scene(
        string title,
        string[] performers,
        int height = 1080,
        double seconds = 600,
        long bytes = 1_000_000_000,
        string codec = "h264",
        double fps = 29.97,
        string? studio = "Pornhub",
        string? site = "pornhub.com",
        string[]? tags = null,
        DateOnly? date = null,
        bool withSheet = true,
        bool hosted = false,
        string? path = null) => new()
    {
        ItemId = Guid.NewGuid(),
        VideoId = title.GetHashCode() & 0x7fffffff,
        Metadata = new PrepMetadata
        {
            Title = title,
            Studio = studio,
            StudioWebsite = site,
            Performers = performers,
            LibraryTags = tags ?? [],
            Date = date,
            MediaInfo = new PrepMediaInfo
            {
                FilePath = path ?? $@"E:\lib\{title}.mp4",
                Width = height * 16 / 9,
                Height = height,
                DurationSeconds = seconds,
                FileSizeBytes = bytes,
                VideoCodec = codec,
                FrameRate = fps,
            },
        },
        Sheet = withSheet
            ? new PrepScreenshot(0, $@"C:\out\{title}\00_contact_sheet.jpg")
            {
                Kind = PrepImageKind.ContactSheet,
                Url = hosted ? $"https://hamsterimg.net/i/{title}.jpg" : null,
                ThumbUrl = hosted ? $"https://hamsterimg.net/i/{title}.th.jpg" : null,
            }
            : null,
    };
}

public sealed class PackLayoutTests
{
    [Fact]
    public void Plan_PutsEachVideoAtTheRootAndItsSheetInScreens()
    {
        var entries = PackLayout.Plan([(@"E:\lib\a\One.mp4", @"C:\out\1\sheet.jpg"), (@"E:\lib\b\Two.mkv", @"C:\out\2\sheet.jpg")]);

        Assert.Equal(["One.mp4"], entries[0].RelativePath);
        Assert.True(entries[0].IsVideo);
        Assert.Equal(["Screens", "One.jpg"], entries[1].RelativePath);
        Assert.False(entries[1].IsVideo);
        Assert.Equal(["Two.mkv"], entries[2].RelativePath);
        Assert.Equal(["Screens", "Two.jpg"], entries[3].RelativePath);
    }

    [Fact]
    public void Plan_RenamesClashingFileNamesAndTheirScreensTogether()
    {
        // Two library folders can hold files with the same name; a torrent cannot.
        var entries = PackLayout.Plan([(@"E:\lib\a\scene.mp4", @"C:\s1.jpg"), (@"E:\lib\b\Scene.mp4", @"C:\s2.jpg")]);

        Assert.Equal(["scene.mp4"], entries[0].RelativePath);
        Assert.Equal(["Scene (2).mp4"], entries[2].RelativePath);
        Assert.Equal(["Screens", "Scene (2).jpg"], entries[3].RelativePath);
    }

    [Fact]
    public void Plan_SkipsTheScreenWhenAVideoHasNone()
    {
        var entries = PackLayout.Plan([(@"E:\lib\a.mp4", null)]);
        Assert.Single(entries);
    }

    [Theory]
    [InlineData(@"E:\test\EMP Packs", null)]                         // beside the library: fine
    [InlineData(@"E:\test\Content\Videos\Packs", "inside")]          // inside a library path
    [InlineData(@"E:\test\Content\Videos", "inside")]                // is a library path
    [InlineData(@"E:\test", "is inside the pack folder")]           // contains a library path
    [InlineData("", "No pack folder")]
    public void DescribeFolderProblem_KeepsPacksOutOfCovesLibrary(string packFolder, string? expected)
    {
        string[] library = [@"E:\test\Content\Videos", @"E:\test\Content\Torrented\MR"];
        var problem = PackLayout.DescribeFolderProblem(packFolder, library);

        if (expected is null)
            Assert.Null(problem);
        else
            Assert.Contains(expected, problem);
    }

    [Fact]
    public void DescribeFolderProblem_IgnoresCaseAndTrailingSeparators()
        => Assert.NotNull(PackLayout.DescribeFolderProblem(@"e:\TEST\content\videos\packs\", [@"E:\test\Content\Videos"]));

    [Fact]
    public void DescribeFolderProblem_DoesNotConfuseASiblingWithSimilarName()
        // "Videos2" is not inside "Videos".
        => Assert.Null(PackLayout.DescribeFolderProblem(@"E:\test\Content\Videos2", [@"E:\test\Content\Videos"]));

    [Fact]
    public void OnOtherDrives_ListsVideosThatCannotBeHardLinked()
    {
        var elsewhere = PackLayout.OnOtherDrives(@"E:\test\EMP Packs", [@"E:\lib\a.mp4", @"D:\other\b.mp4"]);
        Assert.Equal([@"D:\other\b.mp4"], elsewhere);
    }
}

public sealed class PackComposerTests
{
    private static readonly EmpPrepSettings Settings = PackFixtures.Settings;

    private static List<PackScene> TwelveScenes() =>
    [
        .. Enumerable.Range(1, 8).Select(index => PackFixtures.Scene($"Scene {index}", ["Spyder445"], height: 1080,
            seconds: 300 + index * 60, tags: ["Massage", "Solo Female"], date: new DateOnly(2024, 1, index))),
        .. Enumerable.Range(9, 4).Select(index => PackFixtures.Scene($"Scene {index}", ["Spyder445"], height: 2160,
            seconds: 300 + index * 60, codec: "hevc", fps: 60, tags: ["Massage", "Oiled"], date: new DateOnly(2024, 6, 1))),
    ];

    [Fact]
    public void Stats_CountsQualitiesDurationsAndSources()
    {
        var stats = PackComposer.Stats(TwelveScenes());

        Assert.Equal(12, stats.SceneCount);
        Assert.Equal([new CountedValue("4K", 4), new CountedValue("1080p", 8)], stats.Qualities);
        Assert.Equal(360, stats.ShortestSeconds);
        Assert.Equal(1020, stats.LongestSeconds);
        Assert.Equal(690, stats.AverageSeconds);
        Assert.Equal(690, stats.MedianSeconds);
        Assert.Equal(12_000_000_000, stats.TotalSizeBytes);
        Assert.Equal(new CountedValue("h264", 8), stats.Codecs[0]);
        Assert.Equal([new CountedValue("30 fps", 8), new CountedValue("60 fps", 4)], stats.FrameRates);
        Assert.Equal(new DateOnly(2024, 1, 1), stats.EarliestDate);
        Assert.Equal(new DateOnly(2024, 6, 1), stats.LatestDate);
        Assert.Equal([new CountedValue("Spyder445", 12)], stats.Performers);
        Assert.Equal([new CountedValue("pornhub.com", 12)], stats.Websites);
    }

    [Fact]
    public void Stats_CollatesTagsWithHowManyScenesCarryThem()
    {
        var stats = PackComposer.Stats(TwelveScenes());
        Assert.Equal(
            [new CountedValue("massage", 12), new CountedValue("solo.female", 8), new CountedValue("oiled", 4)],
            stats.Tags);
    }

    [Fact]
    public void Title_NamesTheSharedStudioAndTheQualityRange()
        => Assert.Equal(
            "[Pornhub] Spyder445 - Megapack (12 Scenes) [1080p-4K]",
            PackComposer.Title(PackComposer.Stats(TwelveScenes()), Settings));

    [Fact]
    public void Title_SaysVariousForManyPerformersAndDropsAMixedStudio()
    {
        var scenes = new[]
        {
            PackFixtures.Scene("a", ["A"], studio: "One"), PackFixtures.Scene("b", ["B"], studio: "Two"),
            PackFixtures.Scene("c", ["C"], studio: "One"), PackFixtures.Scene("d", ["D"], studio: "One"),
        };
        Assert.Equal("Various - Minipack (4 Scenes) [1080p]", PackComposer.Title(PackComposer.Stats(scenes), Settings));
    }

    [Fact]
    public void Tags_IncludePerformersSitesQualitiesCommonTagsAndPackType()
    {
        var tags = PackComposer.Tags(PackComposer.Stats(TwelveScenes()), Settings);

        Assert.Contains("spyder445", tags);
        Assert.Contains("pornhub.com", tags);
        Assert.Contains("1080p", tags);
        Assert.Contains("2160p", tags);   // tagged the way EMP tags it, though shown as "4K"
        Assert.Contains("massage", tags); // 12/12
        Assert.Contains("solo.female", tags); // 8/12
        Assert.Contains("oiled", tags);   // 4/12 = 33%, above the 25% default
        Assert.Contains("megapack", tags);
    }

    [Fact]
    public void Tags_LeaveOutContentTagsBelowTheThreshold()
    {
        var tags = PackComposer.Tags(PackComposer.Stats(TwelveScenes()), Settings with { PackTagThresholdPercent = 50 });
        Assert.DoesNotContain("oiled", tags);
        Assert.Contains("solo.female", tags);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(50, 50)]
    [InlineData(60, 9)]      // 15%
    [InlineData(1000, 100)]  // capped
    public void ScreensToShow_FollowsEmpsRuleForLargePacks(int scenes, int shown)
        => Assert.Equal(shown, PackComposer.ScreensToShow(scenes).Count);

    [Fact]
    public void Description_HasTheThemeStatsPeopleSourcesTagsAndThumbnails()
    {
        var scenes = TwelveScenes().Select(scene => scene with
        {
            Sheet = scene.Sheet! with { Url = $"https://h/{scene.Metadata.Title}.jpg", ThumbUrl = $"https://h/{scene.Metadata.Title}.th.jpg" },
        }).ToList();
        var stats = PackComposer.Stats(scenes);

        var description = PackComposer.Description(scenes, stats, "Pack Title", "All Spyder445, from Pornhub.", null, Settings);

        Assert.Contains("All Spyder445, from Pornhub.", description);
        Assert.Contains("[table]", description);
        Assert.Contains("4K 33.3% (4) · 1080p 66.7% (8)", description);
        Assert.Contains("shortest 6m 00s · longest 17m 00s · average 11m 30s", description);
        Assert.Contains("Spyder445 (12 scenes)", description);
        Assert.Contains("pornhub.com (12 scenes)", description);
        Assert.Contains("massage (12) · solo.female (8) · oiled (4)", description);
        Assert.Contains("[url=https://h/Scene 1.jpg][img]https://h/Scene 1.th.jpg[/img][/url]", description);
        Assert.DoesNotContain("{", description);
        // No per-scene write-ups.
        Assert.DoesNotContain("Scene 1\n", description);
    }

    [Fact]
    public void Description_NotesWhenOnlyARepresentativeShareOfScreensIsShown()
    {
        var scenes = Enumerable.Range(1, 60).Select(index => PackFixtures.Scene($"S{index}", ["A"])).ToList();
        var description = PackComposer.Description(scenes, PackComposer.Stats(scenes), "T", "theme", null, Settings);

        Assert.Contains("Screens for 9 of 60 scenes shown here", description);
        Assert.Equal(9, description.Split("[NOT UPLOADED:").Length - 1);
    }

    [Fact]
    public void Check_WarnsAboutSmallMinipacksAndAMissingTheme()
    {
        var scenes = Enumerable.Range(1, 5).Select(index => PackFixtures.Scene($"S{index}", ["A"])).ToList();
        var issues = PackComposer.Check(scenes, "", PackComposer.Stats(scenes));

        Assert.Contains(issues, issue => issue.Severity == PackIssue.Warning && issue.Message.Contains("Minipack of 5"));
        Assert.Contains(issues, issue => issue.Message.Contains("theme"));
        Assert.DoesNotContain(issues, issue => issue.Severity == PackIssue.Error);
    }

    [Fact]
    public void Check_BlocksAPackWithScenesMissingScreens()
    {
        var scenes = new[] { PackFixtures.Scene("a", ["A"]), PackFixtures.Scene("b", ["A"], withSheet: false) };
        var issues = PackComposer.Check(scenes, "theme", PackComposer.Stats(scenes));

        Assert.Contains(issues, issue => issue.Severity == PackIssue.Error && issue.Message.Contains("1 scene has no screens"));
    }

    [Fact]
    public void Check_FlagsWhatLooksLikeTheSameSceneTwice()
    {
        var scenes = new[]
        {
            PackFixtures.Scene("Same Scene", ["A"], height: 720, path: @"E:\lib\720.mp4"),
            PackFixtures.Scene("Same Scene", ["A"], height: 1080, path: @"E:\lib\1080.mp4"),
        };
        var issues = PackComposer.Check(scenes, "theme", PackComposer.Stats(scenes));

        Assert.Contains(issues, issue => issue.Message.Contains("\"Same Scene\" appears more than once"));
    }

    [Fact]
    public void Check_IsQuietForAGoodMegapack()
    {
        var scenes = TwelveScenes();
        Assert.Empty(PackComposer.Check(scenes, "All Spyder445 from Pornhub.", PackComposer.Stats(scenes)));
    }
}

/// <summary>Builds a real pack folder with real hard links and hashes it, on the local disk.</summary>
public sealed class PackBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"empprep-pack-{Guid.NewGuid():N}");

    public PackBuildTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, int length, byte seed)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)(index * seed);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Pack_LinksVideosWithoutCopyingAndTheTorrentListsVideosAndScreens()
    {
        // Two "library" videos in different folders, with the same file name, plus their sheets.
        var videoA = Write(@"library\studio-a\scene.mp4", 70_000, 3);
        var videoB = Write(@"library\studio-b\scene.mp4", 50_000, 7);
        var sheetA = Write(@"output\a\00_contact_sheet.jpg", 3_000, 11);
        var sheetB = Write(@"output\b\00_contact_sheet.jpg", 2_000, 13);

        var folder = Path.Combine(_root, "packs", "My Pack");
        var entries = PackLayout.Plan([(videoA, sheetA), (videoB, sheetB)]);
        var targets = PackLayout.Materialize(folder, entries);

        // The pack folder is the torrent's layout.
        Assert.True(File.Exists(Path.Combine(folder, "scene.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "scene (2).mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "Screens", "scene.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "Screens", "scene (2).jpg")));

        // The videos are hard links, not copies: a change through the library name shows up in the
        // pack name, which only holds when both names are the same file.
        await File.WriteAllBytesAsync(videoA, [9, 9, 9]);
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(Path.Combine(folder, "scene.mp4")));
        // ...and the library file is still where Cove expects it.
        Assert.True(File.Exists(videoA));

        // Rebuilding is safe: existing links are recognised, not treated as conflicts.
        PackLayout.Materialize(folder, entries);

        var result = await TorrentBuilder.BuildAsync(new TorrentRequest
        {
            Name = "My Pack",
            SingleFile = false,
            Files = entries.Select((entry, index) => new TorrentFileEntry(targets[index], entry.RelativePath)).ToList(),
            AnnounceUrl = "https://tracker.example/announce",
            Source = "EMP",
        });

        var encoded = Encoding.Latin1.GetString(result.Contents);
        Assert.Contains("4:name7:My Pack", encoded);
        Assert.Contains("7:privatei1e", encoded);
        Assert.Contains("9:scene.mp4", encoded);
        Assert.Contains("7:Screens", encoded);
        Assert.Contains("13:scene (2).mp4", encoded);
        Assert.Equal(3 + 50_000 + 3_000 + 2_000, result.TotalBytes);

        // Deleting the pack folder never touches the library's copies.
        Directory.Delete(folder, recursive: true);
        Assert.True(File.Exists(videoA));
        Assert.True(File.Exists(videoB));
    }
}
