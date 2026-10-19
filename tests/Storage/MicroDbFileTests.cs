using ContractWatcher.MicroDb.Storage;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;

namespace ContractWatcher.MicroDb.Tests.Storage;

public sealed class MicroDbFileTests
{
    [Fact]
    public void Constructor_WhenCreated_UsesCurrentSchemaAndEmptyItems()
    {
        // Act
        var file = new MicroDbFile<TestEntity>();

        // Assert
        Assert.Equal(MicroDbFile<TestEntity>.CurrentSchemaVersion, file.SchemaVersion);
        Assert.Empty(file.Items);
    }

    [Fact]
    public void Properties_WhenInitialized_KeepProvidedValues()
    {
        // Arrange
        var entity = CreateEntity();

        // Act
        var file = new MicroDbFile<TestEntity>
        {
            SchemaVersion = 2,
            Items = [entity]
        };

        // Assert
        Assert.Equal(2, file.SchemaVersion);
        Assert.Same(entity, Assert.Single(file.Items));
    }
}