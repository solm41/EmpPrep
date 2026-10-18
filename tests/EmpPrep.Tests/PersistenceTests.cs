using EmpPrep.Data;
using EmpPrep.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace EmpPrep.Tests;

/// <summary>
/// Runs the item-ownership query against a real relational database and checks that changes made
/// to what it returns are actually saved.
/// </summary>
/// <remarks>
/// This is the gap that let screenshot generation "complete" without ever showing a result: the
/// query returned untracked entities, so SaveChanges had nothing to write and did not complain.
/// Only a round trip through a real database shows that; asserting on the returned object does not.
/// </remarks>
public sealed class PersistenceTests : IDisposable
{
    private sealed class PrepContext(DbContextOptions<PrepContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<PrepBatch>().HasKey(batch => batch.Id);
            model.Entity<PrepItem>().HasKey(item => item.Id);
        }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _batchId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();
    private const int Owner = 7;
    private const int Stranger = 8;

    public PersistenceTests()
    {
        _connection.Open();
        using var db = Create();
        db.Database.EnsureCreated();
        db.Set<PrepBatch>().Add(new PrepBatch { Id = _batchId, UserId = Owner, Name = "b" });
        db.Set<PrepItem>().Add(new PrepItem { Id = _itemId, BatchId = _batchId, VideoId = 1, Status = PrepItemStatus.Pending });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private PrepContext Create() => new(new DbContextOptionsBuilder<PrepContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task ChangesToAnOwnedItem_ArePersisted()
    {
        await using (var db = Create())
        {
            var item = await EmpPrepService.OwnedItemQuery(
                db.Set<PrepItem>(), db.Set<PrepBatch>(), Owner, _itemId, tracking: true).SingleAsync();

            item.Status = PrepItemStatus.Screens;
            item.ScreenshotsJson = """[{"seconds":1,"path":"x.jpg"}]""";

            Assert.Equal(1, await db.SaveChangesAsync());
        }

        // Read back in a fresh context, so the assertion is about the database rather than the cache.
        await using (var db = Create())
        {
            var stored = await db.Set<PrepItem>().AsNoTracking().SingleAsync(item => item.Id == _itemId);
            Assert.Equal(PrepItemStatus.Screens, stored.Status);
            Assert.Contains("x.jpg", stored.ScreenshotsJson);
        }
    }

    [Fact]
    public async Task ChangesPersist_EvenWhenTheContextDefaultsToNoTracking()
    {
        // The host could configure its context not to track by default; the query must not depend on
        // it, because a silent no-op save is the failure mode here.
        var options = new DbContextOptionsBuilder<PrepContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        await using (var db = new PrepContext(options))
        {
            var item = await EmpPrepService.OwnedItemQuery(
                db.Set<PrepItem>(), db.Set<PrepBatch>(), Owner, _itemId, tracking: true).SingleAsync();
            item.Status = PrepItemStatus.Ready;
            Assert.Equal(1, await db.SaveChangesAsync());
        }

        await using (var db = Create())
            Assert.Equal(PrepItemStatus.Ready, (await db.Set<PrepItem>().SingleAsync()).Status);
    }

    [Fact]
    public async Task AnotherUsersItem_IsNotFound()
    {
        await using var db = Create();
        var item = await EmpPrepService.OwnedItemQuery(
            db.Set<PrepItem>(), db.Set<PrepBatch>(), Stranger, _itemId, tracking: true).SingleOrDefaultAsync();

        Assert.Null(item);
    }

    [Fact]
    public async Task ReadOnlyLookups_DoNotTrack()
    {
        await using var db = Create();
        await EmpPrepService.OwnedItemQuery(
            db.Set<PrepItem>(), db.Set<PrepBatch>(), Owner, _itemId, tracking: false).SingleAsync();

        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TheOriginalJoinShape_DidNotTrack()
    {
        // Documents the root cause: AsNoTracking on the joined batches applies to the whole query,
        // so the item that came back was untracked and its changes were never saved.
        await using var db = Create();
        var item = await (
            from candidate in db.Set<PrepItem>()
            join batch in db.Set<PrepBatch>().AsNoTracking() on candidate.BatchId equals batch.Id
            where candidate.Id == _itemId && batch.UserId == Owner
            select candidate).SingleAsync();

        item.Status = PrepItemStatus.Ready;
        Assert.Equal(0, await db.SaveChangesAsync());
    }
}
