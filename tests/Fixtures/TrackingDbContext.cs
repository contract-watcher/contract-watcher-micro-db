using ContractWatcher.MicroDb.Options;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Tests.Fixtures;

internal sealed class TrackingDbContext : MicroDbContext
{
    internal TrackingDbContext(MicroDbOptions options, ChangeTrackingMode trackingMode) : base(options)
    {
        Entities = Set<TrackedTestEntity, int>(
            "tracked-entities",
            static entity => entity.Id,
            trackingMode);
    }

    internal MicroDbSet<TrackedTestEntity, int> Entities { get; }
}