using EmpPrep.Models;
using EmpPrep.Services;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Saving the item form must store only what the user actually changed.
/// </summary>
/// <remarks>
/// The form submits every field. Storing untouched ones froze them: a new tag rule or description
/// template never reached an item that had been saved once.
/// </remarks>
public sealed class StoredEditsTests
{
    private static readonly EmpPrepSettings Settings = new EmpPrepSettings().Normalized();

    private static readonly PrepMetadata Derived = new()
    {
        Title = "Robotic Oil Massage",
        Studio = "Pornhub",
        Performers = ["Spyder445"],
        FormattedTags = ["spyder445", "pornhub.com", "1080p", "massage"],
        MediaInfo = new PrepMediaInfo { Width = 1920, Height = 1080 },
    };

    private static readonly IReadOnlyList<PrepScreenshot> Shots =
        [new PrepScreenshot(0, "/p/sheet.jpg") { Kind = PrepImageKind.ContactSheet, Url = "https://h/sheet.jpg" }];

    /// <summary>What the page shows before any edit — and submits if Save is pressed untouched.</summary>
    private static ItemOverrides AsGenerated()
    {
        var postTitle = MetadataBuilder.RenderPostTitle(Derived, Settings);
        var description = MetadataBuilder.RenderDescription(
            Derived with { PostTitle = postTitle }, Shots, Derived.FormattedTags, Settings);

        return new ItemOverrides(Derived.Title, string.Join(' ', Derived.FormattedTags), description, null)
        {
            PostTitle = postTitle,
        };
    }

    [Fact]
    public void SavingWithoutChangingAnything_StoresNoEdits()
    {
        var edits = EmpPrepService.StoredEdits(Derived, Shots, AsGenerated(), Settings);
        Assert.Equal((null, null, null, null), edits);
    }

    [Fact]
    public void DifferentLineEndingsFromTheTextarea_DoNotCountAsAnEdit()
    {
        var submitted = AsGenerated() with { Description = AsGenerated().Description!.Replace("\n", "\r\n") + "\r\n" };
        Assert.Null(EmpPrepService.StoredEdits(Derived, Shots, submitted, Settings).Description);
    }

    [Fact]
    public void OnlyTheChangedFieldIsStored()
    {
        var submitted = AsGenerated() with { Tags = "spyder445 pornhub.com 1080p massage oil" };
        var edits = EmpPrepService.StoredEdits(Derived, Shots, submitted, Settings);

        Assert.Equal("spyder445 pornhub.com 1080p massage oil", edits.Tags);
        Assert.Null(edits.Title);
        Assert.Null(edits.PostTitle);
        // The description lists the tags nowhere by default, so it is still the generated one.
        Assert.Null(edits.Description);
    }

    [Fact]
    public void AnEditedPostTitle_IsStored()
    {
        var submitted = AsGenerated() with { PostTitle = "My own title" };
        Assert.Equal("My own title", EmpPrepService.StoredEdits(Derived, Shots, submitted, Settings).PostTitle);
    }

    [Fact]
    public void AnEditedSceneTitle_FlowsIntoTheGeneratedPostTitleWithoutFreezingIt()
    {
        // Changing only the scene title: the post title regenerates from it, so the post title the
        // page still shows (built from the old scene title) is not treated as the user's edit...
        var submitted = AsGenerated() with { Title = "New Title" };
        var edits = EmpPrepService.StoredEdits(Derived, Shots, submitted, Settings);

        Assert.Equal("New Title", edits.Title);

        // ...and it is kept as an edit only because it now differs from the regenerated one. A
        // client that re-renders before saving would submit the regenerated title and store none.
        var regenerated = MetadataBuilder.RenderPostTitle(Derived with { Title = "New Title" }, Settings);
        var resubmitted = submitted with { PostTitle = regenerated };
        Assert.Null(EmpPrepService.StoredEdits(Derived, Shots, resubmitted, Settings).PostTitle);
    }

    [Fact]
    public void BlankFields_MeanUseTheGeneratedValue()
    {
        var edits = EmpPrepService.StoredEdits(Derived, Shots, new ItemOverrides(" ", "", null, null) { PostTitle = "  " }, Settings);
        Assert.Equal((null, null, null, null), edits);
    }
}
