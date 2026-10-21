using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractWatcher.MicroDb.Tracking;
using ContractWatcher.MicroDb.Tracking.Abstractions;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Tests.Tracking;

public sealed class ChangeTrackerConcurrencyTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EntityAdded_SameSourcePublishedByAnotherThread_DoesNotDuplicateSubscription()
    {
        using var tracker = new ChangeTracker<ControlledEntity, int>(
            ChangeTrackingMode.Events, SerializerOptions, ImmutableDictionary<int, ControlledEntity>.Empty);

        using var firstSubscribeEntered = new ManualResetEventSlim();
        using var releaseFirstSubscribe = new ManualResetEventSlim();

        var subscribeCalls = 0;

        var entity = new ControlledEntity
        {
            Id = 1,
            OnSubscribe = () =>
            {
                if (Interlocked.Increment(ref subscribeCalls) != 1)
                    return;

                firstSubscribeEntered.Set();
                releaseFirstSubscribe.Wait();
            }
        };

        var first = Task.Run(() => tracker.EntityAdded(entity.Id, entity));

        Assert.True(firstSubscribeEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => tracker.EntityAdded(entity.Id, entity));

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        
        releaseFirstSubscribe.Set();

        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, entity.SubscriberCount);
    }

    [Fact]
    public void EntityAdded_SubscriptionsChangedBeforeCompareExchange_RetriesCas()
    {
        var comparer = new InterleavingKeyComparer();
        var initialItems = ImmutableDictionary<int, ControlledEntity>
            .Empty
            .WithComparers(comparer);

        using var tracker = new ChangeTracker<ControlledEntity, int>(
            ChangeTrackingMode.Events, SerializerOptions, initialItems);

        var competingEntity = new ControlledEntity { Id = 1 };

        var entity = new ControlledEntity
        {
            Id = 1,
            OnSubscribe = comparer.Arm
        };
        
        comparer.OnInterleave = () => tracker.EntityAdded(competingEntity.Id, competingEntity);
        tracker.EntityAdded(entity.Id, entity);
        
        Assert.Equal(1, entity.SubscriberCount);
        Assert.Equal(0, competingEntity.SubscriberCount);
    }

    private sealed class ControlledEntity : IEntityChangeSource
    {
        private EventHandler? _changed;

        public int Id { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Тестовый hook, выполняемый непосредственно
        /// перед регистрацией нового event handler.
        /// </summary>
        [JsonIgnore]
        internal Action? OnSubscribe { get; init; }

        /// <summary>
        /// Текущее количество зарегистрированных handlers.
        /// </summary>
        [JsonIgnore]
        internal int SubscriberCount => Volatile.Read(ref _changed)?.GetInvocationList().Length ?? 0;

        public event EventHandler? Changed
        {
            add
            {
                OnSubscribe?.Invoke();

                EventHandler? current;
                EventHandler? next;
                
                do
                {
                    current = Volatile.Read(ref _changed);
                    next = (EventHandler?)Delegate.Combine(current, value);
                }
                while (!ReferenceEquals(Interlocked.CompareExchange(ref _changed, next, current), current));
            }

            remove
            {
                EventHandler? current;
                EventHandler? next;

                do
                {
                    current = Volatile.Read(ref _changed);
                    next = (EventHandler?)Delegate.Remove(current, value);
                }
                while (!ReferenceEquals(Interlocked.CompareExchange(ref _changed, next, current), current));
            }
        }
    }

    private sealed class InterleavingKeyComparer : IEqualityComparer<int>
    {
        private int _armed;

        internal Action? OnInterleave { get; set; }
        
        internal void Arm() => Volatile.Write(ref _armed, 1);

        public bool Equals(
            int x,
            int y)
        {
            Interleave();

            return x == y;
        }

        public int GetHashCode(int obj)
        {
            Interleave();

            return obj.GetHashCode();
        }

        private void Interleave()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return;

            OnInterleave?.Invoke();
        }
    }
}