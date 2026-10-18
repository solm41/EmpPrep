using Cove.Data;

using EmpPrep;
using EmpPrep.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Verifies the extension's contribution to Cove's PostgreSQL model.
/// </summary>
/// <remarks>
/// A bad <c>ConfigureModel</c> only shows up when the host builds its model at startup, which makes
/// it an extension that fails to load rather than a test failure. Generating the create script
/// exercises the same model-building path without touching a database.
/// </remarks>
public sealed class SchemaTests
{
    private static void WithExtensionModel(Action<CoveContext> assert)
    {
        CoveContext.SetDataExtensions([new EmpPrepExtension()]);
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseNpgsql(
                    "Host=localhost;Database=empprep_model_only;Username=unused;Password=unused",
                    builder => builder.UseVector())
                .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
                .Options;

            using var db = new CoveContext(options);
            assert(db);
        }
        finally
        {
            CoveContext.SetDataExtensions([]);
        }
    }

    [Fact]
    public void Model_MapsBothPrepTables()
        => WithExtensionModel(db =>
        {
            Assert.Equal("empprep_batches", db.Model.FindEntityType(typeof(PrepBatch))!.GetTableName());
            Assert.Equal("empprep_items", db.Model.FindEntityType(typeof(PrepItem))!.GetTableName());
        });

    [Fact]
    public void Model_GeneratesTheExpectedColumnsAndConstraints()
        => WithExtensionModel(db =>
        {
            // Does not connect to a database.
            var script = db.Database.GenerateCreateScript();

            Assert.Contains("empprep_batches", script);
            Assert.Contains("empprep_items", script);
            Assert.Contains("\"ScreenshotsJson\" text NOT NULL", script);
            Assert.Contains("\"AddedAtUtc\" timestamp with time zone NOT NULL", script);
            Assert.Contains("\"InfoHash\" character varying(40)", script);
        });

    [Fact]
    public void Model_KeepsPrepStatePerAccountAndCascadesFromTheLibrary()
        => WithExtensionModel(db =>
        {
            // A batch belongs to a user, and an item to both a batch and a video. Losing the cascade
            // on the video would leave prep rows pointing at files that no longer exist.
            var batch = db.Model.FindEntityType(typeof(PrepBatch))!;
            Assert.Contains(batch.GetForeignKeys(), key => key.DeleteBehavior == DeleteBehavior.Cascade);

            var item = db.Model.FindEntityType(typeof(PrepItem))!;
            Assert.Equal(2, item.GetForeignKeys().Count());
            Assert.All(item.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Cascade, key.DeleteBehavior));
        });

    [Fact]
    public void Model_PreventsTheSameVideoTwiceInOneBatch()
        => WithExtensionModel(db =>
        {
            // Two entries for one video in a batch would build two identical torrents.
            var item = db.Model.FindEntityType(typeof(PrepItem))!;
            Assert.Contains(
                item.GetIndexes(),
                index => index.IsUnique
                    && index.Properties.Select(property => property.Name).OrderBy(name => name)
                        .SequenceEqual(["BatchId", "VideoId"]));
        });

    [Fact]
    public void RawMigration_MatchesTheEntityModelsTables()
    {
        // The host applies the raw SQL, while EF's model is what queries are compiled against. They
        // are written separately, so a table renamed in one and not the other would only fail at
        // runtime, on the first query.
        Assert.Contains("empprep_batches", EmpPrepSchema.Sql);
        Assert.Contains("empprep_items", EmpPrepSchema.Sql);
        Assert.Contains("REFERENCES users(\"Id\") ON DELETE CASCADE", EmpPrepSchema.Sql);
        Assert.Contains("REFERENCES videos(\"Id\") ON DELETE CASCADE", EmpPrepSchema.Sql);
        Assert.Contains("CREATE UNIQUE INDEX", EmpPrepSchema.Sql);

        // Idempotent: the host may retry a migration whose receipt did not commit.
        Assert.DoesNotContain("CREATE TABLE empprep", EmpPrepSchema.Sql);
        Assert.Equal(2, CountOccurrences(EmpPrepSchema.Sql, "CREATE TABLE IF NOT EXISTS"));
    }

    [Fact]
    public void RawMigration_ConstrainsStatusToTheKnownValues()
    {
        Assert.Contains("CHECK (\"Status\" IN ('pending','screens','ready','error'))", EmpPrepSchema.Sql);

        foreach (var status in new[]
                 {
                     PrepItemStatus.Pending, PrepItemStatus.Screens, PrepItemStatus.Ready, PrepItemStatus.Error,
                 })
        {
            Assert.Contains($"'{status}'", EmpPrepSchema.Sql);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
