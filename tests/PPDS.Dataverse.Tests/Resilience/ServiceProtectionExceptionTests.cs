using FluentAssertions;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.Resilience;

/// <summary>
/// Tests for ServiceProtectionException.
/// </summary>
public class ServiceProtectionExceptionTests
{
    #region Error Code Constants Tests

    [Fact]
    public void ErrorCodeRequestsExceeded_HasCorrectValue()
    {
        ServiceProtectionException.ErrorCodeRequestsExceeded.Should().Be(-2147015902);
    }

    [Fact]
    public void ErrorCodeExecutionTimeExceeded_HasCorrectValue()
    {
        ServiceProtectionException.ErrorCodeExecutionTimeExceeded.Should().Be(-2147015903);
    }

    [Fact]
    public void ErrorCodeConcurrentRequestsExceeded_HasCorrectValue()
    {
        ServiceProtectionException.ErrorCodeConcurrentRequestsExceeded.Should().Be(-2147015898);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void MessageConstructor_SetsMessageAndDefaults()
    {
        // Arrange
        const string message = "Custom throttle message";

        // Act
        var ex = new ServiceProtectionException(message);

        // Assert
        ex.Message.Should().Be(message);
        ex.ConnectionName.Should().BeEmpty();
        ex.RetryAfter.Should().Be(TimeSpan.Zero);
        ex.ErrorCode.Should().Be(0);
    }

    [Fact]
    public void FullConstructor_SetsAllProperties()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromSeconds(30);
        const int errorCode = -2147015902;

        // Act
        var ex = new ServiceProtectionException(connectionName, retryAfter, errorCode);

        // Assert
        ex.ConnectionName.Should().Be(connectionName);
        ex.RetryAfter.Should().Be(retryAfter);
        ex.ErrorCode.Should().Be(errorCode);
        ex.Message.Should().Contain(connectionName);
        ex.Message.Should().Contain("30");
    }

    [Fact]
    public void FullConstructorWithInnerException_SetsAllProperties()
    {
        // Arrange
        const string connectionName = "Secondary";
        var retryAfter = TimeSpan.FromSeconds(45);
        const int errorCode = -2147015903;
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var ex = new ServiceProtectionException(connectionName, retryAfter, errorCode, innerException);

        // Assert
        ex.ConnectionName.Should().Be(connectionName);
        ex.RetryAfter.Should().Be(retryAfter);
        ex.ErrorCode.Should().Be(errorCode);
        ex.InnerException.Should().Be(innerException);
        ex.Message.Should().Contain(connectionName);
    }

    #endregion

    #region IsServiceProtectionError Tests

    [Theory]
    [InlineData(-2147015902, true)]  // RequestsExceeded
    [InlineData(-2147015903, true)]  // ExecutionTimeExceeded
    [InlineData(-2147015898, true)]  // ConcurrentRequestsExceeded
    public void IsServiceProtectionError_WithServiceProtectionCodes_ReturnsTrue(int errorCode, bool expected)
    {
        // Act
        var result = ServiceProtectionException.IsServiceProtectionError(errorCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(500)]
    [InlineData(-2147015900)]  // Close but not exact
    [InlineData(-2147015904)]  // Close but not exact
    public void IsServiceProtectionError_WithOtherCodes_ReturnsFalse(int errorCode)
    {
        // Act
        var result = ServiceProtectionException.IsServiceProtectionError(errorCode);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Inheritance Tests

    [Fact]
    public void InheritsFromException()
    {
        // Act
        var ex = new ServiceProtectionException("test");

        // Assert
        ex.Should().BeAssignableTo<Exception>();
    }

    #endregion

    #region Message Formatting Tests

    [Fact]
    public void FullConstructor_Message_IncludesConnectionName()
    {
        // Act
        var ex = new ServiceProtectionException("MyConnection", TimeSpan.FromSeconds(30), -2147015902);

        // Assert
        ex.Message.Should().Contain("MyConnection");
    }

    [Fact]
    public void FullConstructor_Message_IncludesRetryAfter()
    {
        // Act
        var ex = new ServiceProtectionException("Test", TimeSpan.FromMinutes(2), -2147015902);

        // Assert
        ex.Message.Should().Contain("Retry after");
    }

    #endregion
}
