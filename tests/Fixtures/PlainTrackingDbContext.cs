using ContractWatcher.MicroDb.Options;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Tests.Fixtures;

internal sealed class PlainTrackingDbContext : MicroDbContext
{
    internal PlainTrackingDbContext(MicroDbOptions options, ChangeTrackingMode trackingMode) : base(options)
    {
        Entities = Set<PlainTestEntity, int>(
            "plain-entities",
            static entity => entity.Id,
            trackingMode);
    }

    internal MicroDbSet<PlainTestEntity, int> Entities { get; }
}