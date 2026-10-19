using System.Text.Json;
using ContractWatcher.MicroDb.Options;

namespace ContractWatcher.MicroDb.Tests.Options;

public sealed class MicroDbOptionsTests
{
    [Fact]
    public void Constructor_WhenCreated_UsesWebSerializerDefaults()
    {
        // Act
        var options = new MicroDbOptions
        {
            DirectoryPath = "database"
        };

        // Assert
        Assert.Equal("database", options.DirectoryPath);
        Assert.Same(JsonNamingPolicy.CamelCase, options.JsonSerializerOptions.PropertyNamingPolicy);
        Assert.True(options.JsonSerializerOptions.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void JsonSerializerOptions_WhenProvided_UsesProvidedInstance()
    {
        // Arrange
        var serializerOptions = new JsonSerializerOptions();

        // Act
        var options = new MicroDbOptions
        {
            DirectoryPath = "database",
            JsonSerializerOptions = serializerOptions
        };

        // Assert
        Assert.Same(serializerOptions, options.JsonSerializerOptions);
    }
}