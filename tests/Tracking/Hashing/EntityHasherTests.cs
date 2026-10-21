using System.Text.Json;
using ContractWatcher.MicroDb.Tests.Fixtures;
using ContractWatcher.MicroDb.Tracking.Hashing;

namespace ContractWatcher.MicroDb.Tests.Tracking.Hashing;

public sealed class EntityHasherTests
{
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EntityHasher<TrackedTestEntity>(null!));
    }

    [Fact]
    public void Calculate_NullEntity_ThrowsArgumentNullException()
    {
        var hasher = CreateHasher();

        Assert.Throws<ArgumentNullException>(() => hasher.Calculate(null!));
    }

    [Fact]
    public void Calculate_SameState_ReturnsSameFingerprint()
    {
        var hasher = CreateHasher();

        var entity = new TrackedTestEntity
        {
            Id = 1,
            Name = "test"
        };

        var first = hasher.Calculate(entity);
        var second = hasher.Calculate(entity);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Calculate_PersistedPropertyChanged_ReturnsDifferentFingerprint()
    {
        var hasher = CreateHasher();

        var entity = new TrackedTestEntity
        {
            Id = 1,
            Name = "first"
        };

        var first = hasher.Calculate(entity);

        entity.Name = "second";

        var second = hasher.Calculate(entity);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Calculate_JsonIgnoredPropertyChanged_ReturnsSameFingerprint()
    {
        var hasher = CreateHasher();

        var entity =
            new TrackedTestEntity
            {
                Id = 1,
                Name = "test",
                RuntimeValue = "first"
            };

        var first = hasher.Calculate(entity);

        entity.RuntimeValue = "second";

        var second = hasher.Calculate(entity);

        Assert.Equal(first, second);
    }

    private static EntityHasher<TrackedTestEntity> CreateHasher() =>
        new (new JsonSerializerOptions(JsonSerializerDefaults.Web));
}