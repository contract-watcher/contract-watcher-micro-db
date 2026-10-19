using System.Text.Json;
using ContractWatcher.MicroDb.Exceptions;
using ContractWatcher.MicroDb.Storage;
using ContractWatcher.MicroDb.Tests.Fakes;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;

namespace ContractWatcher.MicroDb.Tests.Storage;

public sealed class JsonFileStoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WhenPathIsInvalid_ThrowsArgumentException(string? path)
    {
        // Act
        var action = () => new JsonFileStore<TestEntity>(path!, new());

        // Assert
        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WhenOptionsAreNull_ThrowsArgumentNullException()
    {
        // Arrange
        using var directory = new TestDirectory();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => new JsonFileStore<TestEntity>(directory.FilePath, null!);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void Constructor_WhenPathIsRoot_DoesNotCreateParentDirectory()
    {
        // Arrange
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        // Act
        var store = new JsonFileStore<TestEntity>(root, new());

        // Assert
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task SaveAsync_WhenEntitiesAreNull_ThrowsArgumentNullException()
    {
        // Arrange
        using var directory = new TestDirectory();
        var store = directory.CreateStore();

        // Act
        var action = () => store.SaveAsync(null!);

        // Assert
        await Assert.ThrowsAsync<ArgumentNullException>(action);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"items\":[]}")]
    public void Load_WhenFileFormatIsUnsupported_ThrowsMicroDbException(string json)
    {
        // Arrange
        using var directory = new TestDirectory();
        directory.WriteFile(json);
        var store = directory.CreateStore();

        // Act
        var action = () => store.Load();

        // Assert
        var exception = Assert.Throws<MicroDbException>(action);

        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Load_WhenJsonIsInvalid_WrapsJsonException()
    {
        // Arrange
        using var directory = new TestDirectory();
        directory.WriteFile("{ invalid json");
        var store = directory.CreateStore();

        // Act
        var action = () => store.Load();

        // Assert
        var exception = Assert.Throws<MicroDbException>(action);

        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task SaveAsync_WhenFileExists_ReplacesFileAndRemovesTemporaryFile()
    {
        // Arrange
        using var directory = new TestDirectory();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var store = directory.CreateStore(options);
        options.PropertyNamingPolicy = null;
        await store.SaveAsync([CreateEntity("old")]);
        var entity = CreateEntity("new");

        // Act
        await store.SaveAsync([entity]);

        // Assert
        Assert.Equal(entity, Assert.Single(store.Load()));
        Assert.False(File.Exists(directory.TemporaryFilePath));

        using var document = JsonDocument.Parse(File.ReadAllText(directory.FilePath));

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_WhenSerializationFails_PreservesOriginalFile(bool cancellationException)
    {
        // Arrange
        using var directory = new TestDirectory();

        var original = "{\"schemaVersion\":1,\"items\":[]}";
        directory.WriteFile(original);

        Exception failure = cancellationException
            ? new OperationCanceledException() 
            : new InvalidOperationException("Serialization failed.");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        options.Converters.Add(new CallbackJsonConverter(() => throw failure));

        var store = directory.CreateStore(options);

        // Act
        var action = () => store.SaveAsync([CreateEntity()]);

        // Assert
        var exception = await Assert.ThrowsAsync<MicroDbException>(action);

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(original, File.ReadAllText(directory.FilePath));
        Assert.False(File.Exists(directory.TemporaryFilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenTokenIsCanceled_PropagatesCancellationAndDeletesTemporaryFile()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var source = new CancellationTokenSource();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        options.Converters.Add(new CallbackJsonConverter(() =>
        {
            // ReSharper disable once AccessToDisposedClosure
            source.Cancel();
            // ReSharper disable once AccessToDisposedClosure
            source.Token.ThrowIfCancellationRequested();
        }));

        var store = directory.CreateStore(options);

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => store.SaveAsync([CreateEntity()], source.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        Assert.False(File.Exists(directory.FilePath));
        Assert.False(File.Exists(directory.TemporaryFilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenTemporaryPathIsDirectory_WrapsFailure()
    {
        // Arrange
        using var directory = new TestDirectory();
        var store = directory.CreateStore();
        Directory.CreateDirectory(directory.TemporaryFilePath);

        // Act
        var action = () => store.SaveAsync([CreateEntity()]);

        // Assert
        var exception = await Assert.ThrowsAsync<MicroDbException>(action);

        Assert.NotNull(exception.InnerException);
        Assert.False(File.Exists(directory.FilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenSetIsEmpty_ReloadsEmptySet()
    {
        // Arrange
        using var directory = new TestDirectory();

        var store = directory.CreateStore();

        // Act
        await store.SaveAsync([]);

        // Assert
        Assert.True(File.Exists(directory.FilePath));
        Assert.Empty(store.Load());
        Assert.False(File.Exists(directory.TemporaryFilePath));
    }
}