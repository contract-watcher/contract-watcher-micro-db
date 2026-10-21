using System.Collections.Immutable;
using System.Text.Json;
using ContractWatcher.MicroDb.Tests.Fixtures;
using ContractWatcher.MicroDb.Tracking;
using ContractWatcher.MicroDb.Tracking.Detection;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Tests.Tracking;

public sealed class ChangeTrackerTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Constructor_NullSerializerOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ChangeTracker<TrackedTestEntity, int>(
            ChangeTrackingMode.None,
            null!,
            Empty()));
    }

    [Fact]
    public void Constructor_NullInitialItems_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ChangeTracker<TrackedTestEntity, int>(
            ChangeTrackingMode.None,
            SerializerOptions,
            null!));
    }

    [Fact]
    public void Constructor_InvalidMode_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeTracker<TrackedTestEntity, int>(
            (ChangeTrackingMode)int.MaxValue,
            SerializerOptions,
            Empty()));
    }

    [Fact]
    public void Constructor_EventsForUnsupportedEntity_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => new ChangeTracker<PlainTestEntity, int>(
            ChangeTrackingMode.Events,
            SerializerOptions,
            ImmutableDictionary<int, PlainTestEntity>.Empty));
    }

    [Fact]
    public void Constructor_Events_SubscribesInitialEntities()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        Assert.Equal(1, entity.SubscriberCount);
    }

    [Fact]
    public void GetState_UnknownKey_ReturnsDetached()
    {
        using var tracker = Create(ChangeTrackingMode.Manual);

        Assert.Equal(EntityState.Detached, tracker.GetState(1, Empty()));
    }

    [Fact]
    public void GetState_NewEntity_ReturnsAdded()
    {
        using var tracker = Create(ChangeTrackingMode.Manual);
        var entity = Entity(1);

        Assert.Equal(EntityState.Added, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void GetState_RemovedPersistedEntity_ReturnsDeleted()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Manual, entity);

        Assert.Equal(EntityState.Deleted, tracker.GetState(entity.Id, Empty()));
    }

    [Fact]
    public void GetState_CleanEntity_ReturnsUnchanged()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Manual, entity);

        Assert.Equal(EntityState.Unchanged, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void GetState_DirtyButSameFingerprint_ReturnsUnchanged()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.NotifyChanged();

        Assert.Equal(EntityState.Unchanged, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void GetState_ChangedEntity_ReturnsModified()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.Name = "changed";

        Assert.Equal(EntityState.Modified, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void DetectChanges_ReturnsAddedModifiedAndDeleted()
    {
        var unchanged = Entity(1, "unchanged");
        var modified = Entity(2, "before");
        var deleted = Entity(3, "deleted");

        using var tracker = Create(
            ChangeTrackingMode.Events, unchanged, modified, deleted);

        modified.Name = "after";

        var added = Entity(4, "added");

        tracker.EntityAdded(added.Id, added);

        var current = Items(unchanged, modified, added);
        var result = tracker.DetectChanges(current);

        Assert.Equal(3, result.Count);
        Assert.Equal(EntityState.Modified, result.Changes[2]);
        Assert.Equal(EntityState.Deleted, result.Changes[3]);
        Assert.Equal(EntityState.Added, result.Changes[4]);
        Assert.Contains(2, result.CurrentFingerprints.Keys);
        Assert.Contains(4, result.CurrentFingerprints.Keys);
    }

    [Fact]
    public void DetectChanges_DirtyButUnchanged_AddsVerifiedUnchanged()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.NotifyChanged();

        var result = tracker.DetectChanges(Items(entity));

        Assert.False(result.HasChanges);
        Assert.Contains(entity.Id, result.VerifiedUnchanged);
    }

    [Fact]
    public void DetectChanges_DirtyDeletedPersistedEntity_ReturnsDeleted()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Manual, entity);

        tracker.MarkChanged(entity.Id);

        var result = tracker.DetectChanges(Empty());

        Assert.Equal(EntityState.Deleted, result.Changes[entity.Id]);
    }

    [Fact]
    public void DetectChanges_DirtyKeyMissingFromCurrentAndBaseline_VerifiesAsUnchanged()
    {
        using var tracker = Create(ChangeTrackingMode.Manual);

        tracker.MarkChanged(42);

        var result = tracker.DetectChanges(Empty());

        Assert.False(result.HasChanges);
        Assert.Contains(42, result.VerifiedUnchanged);
    }

    [Fact]
    public void HasChanges_NoChanges_ReturnsFalse()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        Assert.False(tracker.HasChanges(Items(entity)));
    }

    [Fact]
    public void HasChanges_ModifiedEntity_ReturnsTrue()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.Name = "changed";

        Assert.True(tracker.HasChanges(Items(entity)));
    }

    [Fact]
    public void EntityAdded_Events_SubscribesEntity()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events);

        tracker.EntityAdded(entity.Id, entity);

        Assert.Equal(1, entity.SubscriberCount);
    }

    [Fact]
    public void EntityAdded_SameInstanceTwice_DoesNotDuplicateSubscription()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events);

        tracker.EntityAdded(entity.Id, entity);
        tracker.EntityAdded(entity.Id, entity);

        Assert.Equal(1, entity.SubscriberCount);
    }

    [Fact]
    public void EntityUpdated_ReplacesEventSubscription()
    {
        var oldEntity = Entity(1, "old");
        using var tracker = Create(ChangeTrackingMode.Events, oldEntity);
        var newEntity = Entity(1, "new");

        tracker.EntityUpdated(newEntity.Id, newEntity);

        Assert.Equal(0, oldEntity.SubscriberCount);
        Assert.Equal(1, newEntity.SubscriberCount);
    }

    [Fact]
    public void EntityUpdated_Manual_MarksEntityDirty()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Manual, entity);

        entity.Name = "changed";
        tracker.EntityUpdated(entity.Id, entity);

        Assert.Equal(EntityState.Modified, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void EntityRemoved_Events_UnsubscribesEntity()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        tracker.EntityRemoved(entity.Id);

        Assert.Equal(0, entity.SubscriberCount);
        
        tracker.EntityRemoved(entity.Id);

        Assert.Equal(0, entity.SubscriberCount);
    }

    [Fact]
    public void MarkChanged_Manual_MarksEntityDirty()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Manual, entity);

        entity.Name = "changed";
        tracker.MarkChanged(entity.Id);

        Assert.Equal(EntityState.Modified, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void MarkChanged_NotManual_ThrowsInvalidOperationException()
    {
        using var tracker = Create(ChangeTrackingMode.None);

        Assert.Throws<InvalidOperationException>(() => tracker.MarkChanged(1));
    }

    [Fact]
    public void AcceptVerifiedUnchanged_ClearsOldDirtySignal()
    {
        var entity = Entity(1);
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.NotifyChanged();

        var detection = tracker.DetectChanges(Items(entity));

        tracker.AcceptVerifiedUnchanged(detection);
        
        Assert.Equal(EntityState.Unchanged, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void AcceptVerifiedUnchanged_DoesNotClearNewerDirtySignal()
    {
        var entity = Entity(1, "original");
        using var tracker = Create(ChangeTrackingMode.Events, entity);
        
        entity.NotifyChanged();

        var detection = tracker.DetectChanges(Items(entity));
        
        entity.Name = "changed";
        tracker.AcceptVerifiedUnchanged(detection);

        Assert.Equal(EntityState.Modified, tracker.GetState(entity.Id, Items(entity)));
    }

    [Fact]
    public void AcceptChanges_AcceptsCurrentStateAsBaseline()
    {
        var modified = Entity(1, "before");
        var unchanged = Entity(2, "same");

        using var tracker = Create(ChangeTrackingMode.Events, modified, unchanged);

        modified.Name = "after";

        var added = Entity(3, "added");

        tracker.EntityAdded(added.Id, added);

        var current = Items(modified, unchanged, added);

        var detection = tracker.DetectChanges(current);

        tracker.AcceptChanges(current, detection);

        Assert.Equal(EntityState.Unchanged, tracker.GetState(modified.Id, current));
        Assert.Equal(EntityState.Unchanged, tracker.GetState(unchanged.Id, current));
        Assert.Equal(EntityState.Unchanged, tracker.GetState(added.Id, current));
    }

    [Fact]
    public void AcceptChanges_MissingFingerprint_UsesHasherFallback()
    {
        using var tracker = Create(ChangeTrackingMode.Manual);
        var entity = Entity(1, "value");
        var saved = Items(entity);
        var detection = new ChangeDetectionResult<int>([], [], [], []);

        tracker.AcceptChanges(saved, detection);

        Assert.Equal(EntityState.Unchanged, tracker.GetState(entity.Id, saved));
    }

    [Fact]
    public void AcceptChanges_DoesNotClearNewerDirtySignal()
    {
        var entity = Entity(1, "before");
        using var tracker = Create(ChangeTrackingMode.Events, entity);

        entity.Name = "first";

        var current = Items(entity);
        var detection = tracker.DetectChanges(current);

        entity.Name = "second";
        tracker.AcceptChanges(current, detection);

        Assert.Equal(EntityState.Modified, tracker.GetState(entity.Id, current));
    }

    [Fact]
    public void Dispose_UnsubscribesAllEntities()
    {
        var first = Entity(1);
        var second = Entity(2);
        var tracker = Create(ChangeTrackingMode.Events, first, second);

        Assert.Equal(1, first.SubscriberCount);
        Assert.Equal(1, second.SubscriberCount);

        tracker.Dispose();

        Assert.Equal(0, first.SubscriberCount);
        Assert.Equal(0, second.SubscriberCount);
        
        tracker.Dispose();
    }

    [Fact]
    public void CapturedEvent_AfterDispose_IsIgnored()
    {
        var entity = Entity(1);
        var tracker = Create(ChangeTrackingMode.Events, entity);
        var handlers = entity.CaptureHandlers();

        Assert.NotNull(handlers);

        tracker.Dispose();

        handlers(entity, EventArgs.Empty);
    }

    [Fact]
    public void Operations_AfterDispose_ThrowObjectDisposedException()
    {
        var entity = Entity(1);
        var tracker = Create(ChangeTrackingMode.Manual, entity);
        var current = Items(entity);
        var detection = tracker.DetectChanges(current);

        tracker.Dispose();

        Assert.Throws<ObjectDisposedException>(() => tracker.GetState(entity.Id, current));
        Assert.Throws<ObjectDisposedException>(() => tracker.HasChanges(current));
        Assert.Throws<ObjectDisposedException>(() => tracker.DetectChanges(current));
        Assert.Throws<ObjectDisposedException>(() => tracker.EntityAdded(entity.Id, entity));
        Assert.Throws<ObjectDisposedException>(() => tracker.EntityUpdated(entity.Id, entity));
        Assert.Throws<ObjectDisposedException>(() => tracker.EntityRemoved(entity.Id));
        Assert.Throws<ObjectDisposedException>(() => tracker.MarkChanged(entity.Id));
        Assert.Throws<ObjectDisposedException>(() => tracker.AcceptVerifiedUnchanged(detection));
        Assert.Throws<ObjectDisposedException>(() => tracker.AcceptChanges(current, detection));
    }

    private static ChangeTracker<TrackedTestEntity, int> Create(
        ChangeTrackingMode mode, params TrackedTestEntity[] entities) =>
        new (mode, SerializerOptions, Items(entities));

    private static ImmutableDictionary<int, TrackedTestEntity> Items(params TrackedTestEntity[] entities) =>
        entities.ToImmutableDictionary(static x => x.Id);

    private static ImmutableDictionary<int, TrackedTestEntity> Empty() =>
        ImmutableDictionary<int, TrackedTestEntity>.Empty;

    private static TrackedTestEntity Entity(int id, string name = "entity") =>
        new()
        {
            Id = id,
            Name = name
        };
}