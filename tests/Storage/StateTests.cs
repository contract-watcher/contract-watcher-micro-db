using System.Collections;
using System.Text.Json;
using ContractWatcher.MicroDb.Exceptions;
using ContractWatcher.MicroDb.Interfaces;
using ContractWatcher.MicroDb.Storage;
using ContractWatcher.MicroDb.Tests.Fakes;
using ContractWatcher.MicroDb.Tests.Fixtures;
using ContractWatcher.MicroDb.Tracking.Enums;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;
// ReSharper disable AccessToDisposedClosure

namespace ContractWatcher.MicroDb.Tests.Storage;

public sealed class StateTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("upsert")]
    public void Mutation_WhenEntityIsNull_ThrowsArgumentNullException(string operation)
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        // Act
        var action = () => Mutate(state, operation, null!);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
        Assert.False(((IMicroDbSet)state).HasChanges);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("upsert")]
    public void Mutation_WhenKeyIsNull_ThrowsMicroDbException(string operation)
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        // Act
        var action = () => Mutate(state, operation, CreateEntity(id: null!));

        // Assert
        Assert.Throws<MicroDbException>(action);
        Assert.False(((IMicroDbSet)state).HasChanges);
    }

    [Fact]
    public async Task Add_WhenKeyExists_ThrowsWithoutChangingState()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        var entity = CreateEntity();

        state.Add(entity);
        await ((IMicroDbSet)state).SaveChangesAsync();

        // Act
        var action = () => state.Add(CreateEntity(name: "Duplicate"));

        // Assert
        Assert.Throws<InvalidOperationException>(action);
        Assert.Same(entity, state.Find(entity.Id));
        Assert.False(((IMicroDbSet)state).HasChanges);
    }

    [Fact]
    public void Update_WhenKeyDoesNotExist_ThrowsWithoutChangingState()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        // Act
        var action = () => state.Update(CreateEntity());

        // Assert
        Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(0, state.Count);
        Assert.False(((IMicroDbSet)state).HasChanges);
    }

    [Theory]
    [InlineData("{\"items\":[null]}")]
    [InlineData("{\"items\":[{\"id\":null}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\"},{\"id\":\"a\"}]}")]
    public void Constructor_WhenStoredEntitiesAreInvalid_ThrowsMicroDbException(string json)
    {
        // Arrange
        using var directory = new TestDirectory();
        directory.WriteFile(json);

        // Act
        var action = () => directory.CreateState();

        // Assert
        Assert.Throws<MicroDbException>(action);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenStateIsUnchanged_ReturnsZero()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        // Act
        var result = await ((IMicroDbSet)state).SaveChangesAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.False(File.Exists(directory.FilePath));
        Assert.False(state.Remove("missing"));
        Assert.False(((IMicroDbSet)state).HasChanges);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenWriteFails_KeepsChangesForRetry()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        state.Add(CreateEntity());
        Directory.CreateDirectory(directory.TemporaryFilePath);

        var set = (IMicroDbSet)state;

        // Act
        var action = () => set.SaveChangesAsync();

        // Assert
        await Assert.ThrowsAsync<MicroDbException>(action);
        Assert.True(set.HasChanges);

        Directory.Delete(directory.TemporaryFilePath);

        Assert.Equal(1, await set.SaveChangesAsync());
        Assert.Equal(0, await set.SaveChangesAsync());
        Assert.False(set.HasChanges);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenCanceled_KeepsChangesAndAllowsRetry()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();
        using var source = new CancellationTokenSource();

        state.Add(CreateEntity());
        source.Cancel();

        var set = (IMicroDbSet)state;

        // Act
        var action = () => set.SaveChangesAsync(source.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.True(set.HasChanges);
        Assert.Equal(1, await set.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WhenMutationOccursDuringSerialization_PreservesNewChanges()
    {
        // Arrange
        using var directory = new TestDirectory();

        State<TestEntity, string>? state = null;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var mutate = true;

        options.Converters.Add(new CallbackJsonConverter(() =>
        {
            if (!mutate)
                return;

            mutate = false;
            // ReSharper disable once AccessToModifiedClosure
            state!.Add(CreateEntity("customers"));
        }));

        using var ownedState = directory.CreateState(options);
        state = ownedState;

        state.Add(CreateEntity());

        var set = (IMicroDbSet)state;

        // Act
        var result = await set.SaveChangesAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.True(set.HasChanges);
        Assert.Single(directory.CreateStore().Load());
        Assert.Equal(2, state.Count);

        Assert.Equal(1, await set.SaveChangesAsync());
        Assert.False(set.HasChanges);
        Assert.Equal(2, directory.CreateStore().Load().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_WhenSnapshotChangesBeforePublication_RetriesWithoutLosingChanges(bool remove)
    {
        // Arrange
        using var directory = new TestDirectory();

        var firstKey = new CallbackKey(1);
        var secondKey = new CallbackKey(2);

        using var state = new State<TestEntity, CallbackKey>(
            entity => entity.Id == "first" ? firstKey : secondKey, 
            directory.CreateStore(), 
            new(JsonSerializerDefaults.Web), 
            ChangeTrackingMode.None);

        state.Add(CreateEntity("first"));

        firstKey.OnHash = () => state.Add(CreateEntity("second"));

        // Act
        if (remove)
            Assert.True(state.Remove(firstKey));
        else
            state.Update(CreateEntity("first", "Updated"));

        // Assert
        Assert.True(state.Contains(secondKey));
        Assert.Equal(remove ? 1 : 2, state.Count);
        Assert.Equal(remove ? null : "Updated", state.Find(firstKey)?.Name);

        Assert.Equal(3, await ((IMicroDbSet)state).SaveChangesAsync());
    }

    [Fact]
    public void GetEnumerator_WhenCalledThroughNonGenericInterface_ReturnsEntities()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        var entity = CreateEntity();
        state.Add(entity);

        // Act
        var enumerator = ((IEnumerable)state).GetEnumerator();

        // Assert
        Assert.True(enumerator.MoveNext());
        Assert.Same(entity, enumerator.Current);
        Assert.False(enumerator.MoveNext());

        (enumerator as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Dispose_WhenCalledRepeatedly_RejectsAllOperations()
    {
        // Arrange
        using var directory = new TestDirectory();

        var state = directory.CreateState();
        var set = (IMicroDbSet)state;

        // Act
        state.Dispose();
        state.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => state.Count);
        Assert.Throws<ObjectDisposedException>(() => set.HasChanges);
        Assert.Throws<ObjectDisposedException>(() => state.Find("products"));
        Assert.Throws<ObjectDisposedException>(() => state.Contains("products"));
        Assert.Throws<ObjectDisposedException>(() => state.Add(CreateEntity()));
        Assert.Throws<ObjectDisposedException>(() => state.Update(CreateEntity()));
        Assert.Throws<ObjectDisposedException>(() => state.Upsert(CreateEntity()));
        Assert.Throws<ObjectDisposedException>(() => state.Remove("products"));
        Assert.Throws<ObjectDisposedException>(() => state.GetEnumerator());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => set.SaveChangesAsync());
    }
    
    [Fact]
    public void Constructor_NullKeySelector_ThrowsArgumentNullException()
    {
        using var directory = new TemporaryDirectory();
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var fileStore = new JsonFileStore<TestEntity>(
            Path.Combine(directory.Path, "entities.json"), serializerOptions);

        var exception = Assert.Throws<ArgumentNullException>(() => new State<TestEntity, int>(
            null!, fileStore, serializerOptions, ChangeTrackingMode.None));

        Assert.Equal("keySelector", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullFileStore_ThrowsArgumentNullException()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var exception = Assert.Throws<ArgumentNullException>(() => new State<TestEntity, string>(
            static entity => entity.Id, null!, serializerOptions, ChangeTrackingMode.None));

        Assert.Equal("fileStore", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullSerializerOptions_ThrowsArgumentNullException()
    {
        using var directory = new TemporaryDirectory();

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var fileStore = new JsonFileStore<TestEntity>(
            Path.Combine(directory.Path, "entities.json"), serializerOptions);

        var exception = Assert.Throws<ArgumentNullException>(() => new State<TestEntity, string>(
            static entity => entity.Id, fileStore, null!, ChangeTrackingMode.None));

        Assert.Equal("serializerOptions", exception.ParamName);
    }

    private static void Mutate(State<TestEntity, string> state, string operation, TestEntity entity)
    {
        switch (operation)
        {
            case "add":
                state.Add(entity);
                break;

            case "update":
                state.Update(entity);
                break;

            case "upsert":
                state.Upsert(entity);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}