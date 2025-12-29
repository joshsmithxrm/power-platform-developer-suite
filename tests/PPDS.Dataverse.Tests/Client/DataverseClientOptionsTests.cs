using FluentAssertions;
using PPDS.Dataverse.Client;
using Xunit;

namespace PPDS.Dataverse.Tests.Client;

/// <summary>
/// Tests for DataverseClientOptions.
/// </summary>
public class DataverseClientOptionsTests
{
    #region Default Constructor Tests

    [Fact]
    public void DefaultConstructor_CallerId_IsNull()
    {
        // Act
        var options = new DataverseClientOptions();

        // Assert
        options.CallerId.Should().BeNull();
    }

    [Fact]
    public void DefaultConstructor_CallerAADObjectId_IsNull()
    {
        // Act
        var options = new DataverseClientOptions();

        // Assert
        options.CallerAADObjectId.Should().BeNull();
    }

    [Fact]
    public void DefaultConstructor_MaxRetryCount_IsNull()
    {
        // Act
        var options = new DataverseClientOptions();

        // Assert
        options.MaxRetryCount.Should().BeNull();
    }

    [Fact]
    public void DefaultConstructor_RetryPauseTime_IsNull()
    {
        // Act
        var options = new DataverseClientOptions();

        // Assert
        options.RetryPauseTime.Should().BeNull();
    }

    #endregion

    #region CallerId Constructor Tests

    [Fact]
    public void CallerIdConstructor_SetsCallerId()
    {
        // Arrange
        var callerId = Guid.NewGuid();

        // Act
        var options = new DataverseClientOptions(callerId);

        // Assert
        options.CallerId.Should().Be(callerId);
    }

    [Fact]
    public void CallerIdConstructor_OtherPropertiesRemainNull()
    {
        // Arrange
        var callerId = Guid.NewGuid();

        // Act
        var options = new DataverseClientOptions(callerId);

        // Assert
        options.CallerAADObjectId.Should().BeNull();
        options.MaxRetryCount.Should().BeNull();
        options.RetryPauseTime.Should().BeNull();
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void CallerId_CanBeSet()
    {
        // Arrange
        var callerId = Guid.NewGuid();

        // Act
        var options = new DataverseClientOptions { CallerId = callerId };

        // Assert
        options.CallerId.Should().Be(callerId);
    }

    [Fact]
    public void CallerAADObjectId_CanBeSet()
    {
        // Arrange
        var aadObjectId = Guid.NewGuid();

        // Act
        var options = new DataverseClientOptions { CallerAADObjectId = aadObjectId };

        // Assert
        options.CallerAADObjectId.Should().Be(aadObjectId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void MaxRetryCount_CanBeSet(int retryCount)
    {
        // Act
        var options = new DataverseClientOptions { MaxRetryCount = retryCount };

        // Assert
        options.MaxRetryCount.Should().Be(retryCount);
    }

    [Fact]
    public void RetryPauseTime_CanBeSet()
    {
        // Arrange
        var pauseTime = TimeSpan.FromSeconds(5);

        // Act
        var options = new DataverseClientOptions { RetryPauseTime = pauseTime };

        // Assert
        options.RetryPauseTime.Should().Be(pauseTime);
    }

    #endregion

    #region Combined Options Tests

    [Fact]
    public void AllProperties_CanBeSetTogether()
    {
        // Arrange
        var callerId = Guid.NewGuid();
        var aadObjectId = Guid.NewGuid();
        var retryCount = 5;
        var pauseTime = TimeSpan.FromSeconds(10);

        // Act
        var options = new DataverseClientOptions
        {
            CallerId = callerId,
            CallerAADObjectId = aadObjectId,
            MaxRetryCount = retryCount,
            RetryPauseTime = pauseTime
        };

        // Assert
        options.CallerId.Should().Be(callerId);
        options.CallerAADObjectId.Should().Be(aadObjectId);
        options.MaxRetryCount.Should().Be(retryCount);
        options.RetryPauseTime.Should().Be(pauseTime);
    }

    [Fact]
    public void ImpersonationOptions_TypicalUsage()
    {
        // Arrange - typical impersonation scenario
        var targetUserId = Guid.NewGuid();

        // Act
        var options = new DataverseClientOptions(targetUserId);

        // Assert
        options.CallerId.Should().Be(targetUserId);
        // AAD object ID typically not set when using CallerId
        options.CallerAADObjectId.Should().BeNull();
    }

    [Fact]
    public void RetryOptions_TypicalUsage()
    {
        // Arrange - typical retry configuration
        var options = new DataverseClientOptions
        {
            MaxRetryCount = 3,
            RetryPauseTime = TimeSpan.FromSeconds(2)
        };

        // Assert
        options.MaxRetryCount.Should().Be(3);
        options.RetryPauseTime.Should().Be(TimeSpan.FromSeconds(2));
    }

    #endregion
}
