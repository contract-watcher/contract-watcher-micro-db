using System.Collections;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;

namespace ContractWatcher.MicroDb.Tests;

public sealed class MicroDbSetTests
{
    [Fact]
    public void Constructor_WhenKeySelectorIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        using var directory = new TestDirectory();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => 
            new MicroDbSet<TestEntity, string>(null!, directory.CreateStore());

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void Constructor_WhenFileStoreIsNull_ThrowsArgumentNullException()
    {
        // Act
        var action = () => 
            new MicroDbSet<TestEntity, string>(entity => entity.Id, null!);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void Add_WhenEntityDoesNotExist_StoresEntity()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        var set = context.Register();
        var entity = CreateEntity();

        // Act
        set.Add(entity);

        // Assert
        Assert.Equal(1, set.Count);
        Assert.Same(entity, set.Find(entity.Id));
        Assert.True(set.Contains(entity.Id));
        Assert.Null(set.Find("missing"));
        Assert.False(set.Contains("missing"));
        Assert.True(context.HasChanges);
    }

    [Fact]
    public void Update_WhenEntityExists_ReplacesEntity()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();
        var set = context.Register();
        set.Add(CreateEntity());
        var updated = CreateEntity(name: "Updated");

        // Act
        set.Update(updated);

        // Assert
        Assert.Same(updated, set.Find(updated.Id));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Upsert_WhenEntityExistsOrIsMissing_StoresLatestEntity()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();
        var set = context.Register();
        var updated = CreateEntity(name: "Updated");

        // Act
        set.Upsert(CreateEntity());
        set.Upsert(updated);

        // Assert
        Assert.Same(updated, set.Find(updated.Id));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Remove_WhenEntityExists_RemovesEntity()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();
        var set = context.Register();
        set.Add(CreateEntity());

        // Act
        var removed = set.Remove("products");
        var removedAgain = set.Remove("products");

        // Assert
        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.Empty(set);
    }

    [Fact]
    public void GetEnumerator_WhenSetChanges_EnumeratesCapturedSnapshot()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();
        var set = context.Register();
        var entity = CreateEntity();
        set.Add(entity);
        using var enumerator = set.GetEnumerator();
        var untyped = ((IEnumerable)set).GetEnumerator();

        // Act
        set.Remove(entity.Id);

        // Assert
        Assert.True(enumerator.MoveNext());
        Assert.Same(entity, enumerator.Current);
        Assert.False(enumerator.MoveNext());
        Assert.True(untyped.MoveNext());
        Assert.Same(entity, untyped.Current);
        Assert.False(untyped.MoveNext());

        (untyped as IDisposable)?.Dispose();
    }
}