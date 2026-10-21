using ContractWatcher.MicroDb.Options;
using ContractWatcher.MicroDb.Tests.Fixtures;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Tests.Tracking;

public sealed class ChangeTrackingIntegrationTests
{
    [Fact]
    public async Task Events_PropertyChanged_IsPersistedWithoutExplicitUpdate()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory);

        await usingContext(async db =>
        {
            db.Entities.Add(new TrackedTestEntity
            {
                Id = 1,
                Name = "before"
            });

            await db.SaveChangesAsync();

            var entity = db.Entities.Find(1)!;

            entity.Name = "after";

            Assert.True(db.HasChanges);
            Assert.Equal(EntityState.Modified, db.Entities.GetState(1));

            var changes = await db.SaveChangesAsync();

            Assert.Equal(1, changes);
            Assert.False(db.HasChanges);
            Assert.Equal(EntityState.Unchanged, db.Entities.GetState(1));
        });

        using var restored = new TrackingDbContext(options, ChangeTrackingMode.Events);

        Assert.Equal("after", restored.Entities.Find(1)!.Name);

        return;

        async Task usingContext(Func<TrackingDbContext, Task> action)
        {
            using var db = new TrackingDbContext(options, ChangeTrackingMode.Events);

            await action(db);
        }
    }

    [Fact]
    public async Task Events_EntityReturnedToBaseline_DoesNotPersist()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory);

        using var db = new TrackingDbContext(options, ChangeTrackingMode.Events);

        db.Entities.Add(new TrackedTestEntity
        {
            Id = 1,
            Name = "original"
        });

        await db.SaveChangesAsync();

        var entity = db.Entities.Find(1)!;

        entity.Name = "changed";
        entity.Name = "original";

        Assert.False(db.HasChanges);

        var saved = await db.SaveChangesAsync();

        Assert.Equal(0, saved);
        Assert.Equal(EntityState.Unchanged, db.Entities.GetState(1));
    }

    [Fact]
    public async Task Manual_PropertyChangedWithoutMarkChanged_IsIgnored()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory);

        using (var db = new TrackingDbContext(options, ChangeTrackingMode.Manual))
        {
            db.Entities.Add(new TrackedTestEntity
            {
                Id = 1,
                Name = "before"
            });

            await db.SaveChangesAsync();

            var entity = db.Entities.Find(1)!;
            entity.Name = "after";

            Assert.False(db.HasChanges);
            Assert.Equal(0, await db.SaveChangesAsync());
        }

        using var restored = new TrackingDbContext(options, ChangeTrackingMode.Manual);
        Assert.Equal("before", restored.Entities.Find(1)!.Name);
    }

    [Fact]
    public async Task Manual_MarkChanged_PersistsDirectMutation()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory);

        using (var db = new TrackingDbContext(options, ChangeTrackingMode.Manual))
        {
            db.Entities.Add(new TrackedTestEntity
            {
                Id = 1,
                Name = "before"
            });

            await db.SaveChangesAsync();

            var entity = db.Entities.Find(1)!;

            entity.Name = "after";
            db.Entities.MarkChanged(entity.Id);

            Assert.True(db.HasChanges);
            Assert.Equal(EntityState.Modified, db.Entities.GetState(entity.Id));
            Assert.Equal(1, await db.SaveChangesAsync());
        }

        using var restored = new TrackingDbContext(options, ChangeTrackingMode.Manual);

        Assert.Equal("after", restored.Entities.Find(1)!.Name);
    }

    [Fact]
    public async Task None_DirectMutation_IsNotTracked()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory);

        using (var db = new TrackingDbContext(options, ChangeTrackingMode.None))
        {
            db.Entities.Add(new TrackedTestEntity
            {
                Id = 1,
                Name = "before"
            });

            await db.SaveChangesAsync();

            db.Entities.Find(1)!.Name = "after";

            Assert.False(db.HasChanges);
            Assert.Equal(0, await db.SaveChangesAsync());
        }

        using var restored = new TrackingDbContext(options, ChangeTrackingMode.None);

        Assert.Equal("before", restored.Entities.Find(1)!.Name);
    }

    [Fact]
    public async Task GetState_FollowsEntityLifecycle()
    {
        using var directory = new TemporaryDirectory();
        using var db = new TrackingDbContext(Options(directory), ChangeTrackingMode.Events);

        var entity = new TrackedTestEntity
        {
            Id = 1,
            Name = "test"
        };

        Assert.Equal(EntityState.Detached, db.Entities.GetState(1));

        db.Entities.Add(entity);

        Assert.Equal(EntityState.Added, db.Entities.GetState(1));

        await db.SaveChangesAsync();

        Assert.Equal(EntityState.Unchanged, db.Entities.GetState(1));

        entity.Name = "changed";

        Assert.Equal(EntityState.Modified, db.Entities.GetState(1));

        await db.SaveChangesAsync();

        Assert.True(db.Entities.Remove(1));
        Assert.Equal(EntityState.Deleted, db.Entities.GetState(1));

        await db.SaveChangesAsync();

        Assert.Equal(EntityState.Detached, db.Entities.GetState(1));
    }

    [Fact]
    public void Manual_MarkChanged_MissingEntity_ThrowsKeyNotFoundException()
    {
        using var directory = new TemporaryDirectory();
        using var db = new TrackingDbContext(Options(directory), ChangeTrackingMode.Manual);

        Assert.Throws<KeyNotFoundException>(() => db.Entities.MarkChanged(42));
    }

    [Fact]
    public void MarkChanged_NonManualMode_ThrowsInvalidOperationException()
    {
        using var directory = new TemporaryDirectory();

        using var db = new TrackingDbContext(Options(directory), ChangeTrackingMode.Events);

        db.Entities.Add(
            new TrackedTestEntity
            {
                Id = 1,
                Name = "test"
            });

        Assert.Throws<InvalidOperationException>(() => db.Entities.MarkChanged(1));
    }

    [Fact]
    public void Events_EntityWithoutChangeSource_ThrowsInvalidOperationException()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<InvalidOperationException>(
            () => new PlainTrackingDbContext(Options(directory), ChangeTrackingMode.Events));
    }

    [Fact]
    public void TrackingOperations_AfterDispose_ThrowObjectDisposedException()
    {
        using var directory = new TemporaryDirectory();
        var db = new TrackingDbContext(Options(directory), ChangeTrackingMode.Manual);

        db.Entities.Add(new TrackedTestEntity
        {
            Id = 1,
            Name = "test"
        });

        db.Dispose();

        Assert.Throws<ObjectDisposedException>(() => db.Entities.GetState(1));
        Assert.Throws<ObjectDisposedException>(() => db.Entities.MarkChanged(1));
    }

    private static MicroDbOptions Options(TemporaryDirectory directory) =>
        new (){ DirectoryPath = directory.Path };
}