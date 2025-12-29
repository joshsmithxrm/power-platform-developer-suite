using FluentAssertions;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

/// <summary>
/// Tests for PoolExhaustedException.
/// </summary>
public class PoolExhaustedExceptionTests
{
    #region Constructor Tests

    [Fact]
    public void DefaultConstructor_HasDefaultMessage()
    {
        // Act
        var ex = new PoolExhaustedException();

        // Assert
        ex.Message.Should().Be("Connection pool exhausted.");
        ex.ActiveConnections.Should().Be(0);
        ex.MaxPoolSize.Should().Be(0);
        ex.AcquireTimeout.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void MessageConstructor_SetsMessage()
    {
        // Arrange
        const string message = "Custom error message";

        // Act
        var ex = new PoolExhaustedException(message);

        // Assert
        ex.Message.Should().Be(message);
    }

    [Fact]
    public void MessageAndInnerExceptionConstructor_SetsBoth()
    {
        // Arrange
        const string message = "Outer error";
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var ex = new PoolExhaustedException(message, innerException);

        // Assert
        ex.Message.Should().Be(message);
        ex.InnerException.Should().Be(innerException);
    }

    [Fact]
    public void FullConstructor_SetsAllProperties()
    {
        // Arrange
        const int activeConnections = 50;
        const int maxPoolSize = 52;
        var acquireTimeout = TimeSpan.FromSeconds(30);

        // Act
        var ex = new PoolExhaustedException(activeConnections, maxPoolSize, acquireTimeout);

        // Assert
        ex.ActiveConnections.Should().Be(activeConnections);
        ex.MaxPoolSize.Should().Be(maxPoolSize);
        ex.AcquireTimeout.Should().Be(acquireTimeout);
        ex.Message.Should().Contain("Active: 50");
        ex.Message.Should().Contain("MaxPoolSize: 52");
        ex.Message.Should().Contain("30.0s");
    }

    #endregion

    #region Inheritance Tests

    [Fact]
    public void InheritsFromTimeoutException()
    {
        // Act
        var ex = new PoolExhaustedException();

        // Assert
        ex.Should().BeAssignableTo<TimeoutException>();
    }

    [Fact]
    public void InheritsFromException()
    {
        // Act
        var ex = new PoolExhaustedException();

        // Assert
        ex.Should().BeAssignableTo<Exception>();
    }

    #endregion

    #region Message Formatting Tests

    [Fact]
    public void FullConstructor_Message_ContainsActionableAdvice()
    {
        // Act
        var ex = new PoolExhaustedException(10, 50, TimeSpan.FromSeconds(30));

        // Assert
        ex.Message.Should().Contain("Consider increasing MaxPoolSize");
    }

    [Theory]
    [InlineData(0, 10, 5.0, "Active: 0")]
    [InlineData(100, 100, 60.0, "Active: 100")]
    [InlineData(52, 52, 30.0, "Active: 52")]
    public void FullConstructor_Message_FormatsValuesCorrectly(int active, int max, double timeoutSecs, string expectedContent)
    {
        // Act
        var ex = new PoolExhaustedException(active, max, TimeSpan.FromSeconds(timeoutSecs));

        // Assert
        ex.Message.Should().Contain(expectedContent);
    }

    #endregion
}
