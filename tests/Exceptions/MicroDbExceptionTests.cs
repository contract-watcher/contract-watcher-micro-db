using ContractWatcher.MicroDb.Exceptions;

namespace ContractWatcher.MicroDb.Tests.Exceptions;

public sealed class MicroDbExceptionTests
{
    [Fact]
    public void Constructor_WhenMessageProvided_PreservesMessage()
    {
        // Act
        var exception = new MicroDbException("Storage failed.");

        // Assert
        Assert.Equal("Storage failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Constructor_WhenInnerExceptionProvided_PreservesOriginalException()
    {
        // Arrange
        var innerException = new IOException("Disk failed.");

        // Act
        var exception = new MicroDbException("Storage failed.", innerException);

        // Assert
        Assert.Equal("Storage failed.", exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }
}