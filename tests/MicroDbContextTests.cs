using System.Text.Json;
using ContractWatcher.MicroDb.Exceptions;
using ContractWatcher.MicroDb.Options;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;
// ReSharper disable AccessToDisposedClosure

namespace ContractWatcher.MicroDb.Tests;

public sealed class MicroDbContextTests
{
    [Fact]
    public void Constructor_WhenOptionsAreNull_ThrowsArgumentNullException()
    {
        // Act
        var action = () => new TestContext(null!);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WhenDirectoryPathIsInvalid_ThrowsArgumentException(
        string? path)
    {
        // Arrange
        var options = new MicroDbOptions { DirectoryPath = path! };

        // Act
        var action = () => new TestContext(options);

        // Assert
        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WhenSerializerOptionsAreNull_ThrowsArgumentNullException()
    {
        // Arrange
        using var directory = new TestDirectory();

        var options = new MicroDbOptions
        {
            DirectoryPath = directory.DirectoryPath,
            JsonSerializerOptions = null!
        };

        // Act
        var action = () => new TestContext(options);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Fact]
    public void Constructor_WhenDirectoryDoesNotExist_CreatesDirectory()
    {
        // Arrange
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.DirectoryPath, "nested");

        // Act
        using var context = new TestContext(new() { DirectoryPath = path });

        // Assert
        Assert.True(Directory.Exists(path));
        Assert.False(context.HasChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../products")]
    [InlineData("products.json")]
    [InlineData("products/users")]
    public void Set_WhenNameIsInvalid_ThrowsArgumentException(string? name)
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => context.Register(name!);

        // Assert
        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Set_WhenKeySelectorIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => context.RegisterWithSelector(null!);

        // Assert
        Assert.Throws<ArgumentNullException>(action);
    }

    [Theory]
    [InlineData("products")]
    [InlineData("PRODUCTS")]
    public void Set_WhenNameIsAlreadyRegistered_ThrowsInvalidOperationException(
        string name)
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        context.Register();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => context.Register(name);

        // Assert
        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenSetsContainChanges_SavesOnlyChangedSets()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        context.Register("clean");
        context.Register("products-1_test").Add(CreateEntity());
        context.Register("customers").Add(CreateEntity("customers"));

        // Act
        var hasChanges = context.HasChanges;
        var result = await context.SaveChangesAsync();
        var repeatedResult = await context.SaveChangesAsync();

        // Assert
        Assert.True(hasChanges);
        Assert.Equal(2, result);
        Assert.Equal(0, repeatedResult);
        Assert.False(context.HasChanges);

        Assert.False(File.Exists(
            Path.Combine(directory.DirectoryPath, "clean.json")));

        using var reopened = directory.CreateContext();

        Assert.Equal(
            "Product",
            reopened.Register("products-1_test").Find("products")!.Name);

        Assert.True(reopened.Register("customers").Contains("customers"));
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTokenIsCanceled_PreservesChanges()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();
        using var source = new CancellationTokenSource();

        context.Register().Add(CreateEntity());
        source.Cancel();

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => context.SaveChangesAsync(source.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        Assert.True(context.HasChanges);
        Assert.False(File.Exists(directory.FilePath));
    }

    [Fact]
    public async Task SaveChangesAsync_WhenLaterSetFails_KeepsEarlierSetSaved()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var context = directory.CreateContext();

        context.Register().Add(CreateEntity());
        context.Register("broken").Add(CreateEntity());

        var blockedPath = Path.Combine(
            directory.DirectoryPath,
            "broken.json.tmp");

        Directory.CreateDirectory(blockedPath);

        // Act
        // ReSharper disable once AccessToDisposedClosure
        var action = () => context.SaveChangesAsync();

        // Assert
        await Assert.ThrowsAsync<MicroDbException>(action);

        Assert.True(File.Exists(directory.FilePath));
        Assert.True(context.HasChanges);

        Directory.Delete(blockedPath);

        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.False(context.HasChanges);
    }

    [Fact]
    public async Task Constructor_WhenOriginalOptionsChange_UsesCopiedOptions()
    {
        // Arrange
        using var directory = new TestDirectory();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var context = directory.CreateContext(options);

        options.PropertyNamingPolicy = null;
        context.Register().Add(CreateEntity());

        // Act
        await context.SaveChangesAsync();

        // Assert
        using var document = JsonDocument.Parse(
            File.ReadAllText(directory.FilePath));

        Assert.True(document.RootElement.TryGetProperty("schemaVersion", out _));
    }

    [Fact]
    public async Task Dispose_WhenCalledRepeatedly_DisposesSetsWithoutSaving()
    {
        // Arrange
        using var directory = new TestDirectory();

        var context = directory.CreateContext();
        var set = context.Register();

        set.Add(CreateEntity());

        // Act
        context.Dispose();
        context.Dispose();

        // Assert
        Assert.False(File.Exists(directory.FilePath));

        Assert.Throws<ObjectDisposedException>(() => context.HasChanges);
        Assert.Throws<ObjectDisposedException>(() => context.Register());
        Assert.Throws<ObjectDisposedException>(() => set.Count);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => context.SaveChangesAsync());
    }
}