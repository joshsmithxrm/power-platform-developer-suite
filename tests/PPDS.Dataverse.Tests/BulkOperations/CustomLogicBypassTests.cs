using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

/// <summary>
/// Tests for CustomLogicBypass enum.
/// </summary>
public class CustomLogicBypassTests
{
    #region Value Tests

    [Fact]
    public void None_HasValueZero()
    {
        ((int)CustomLogicBypass.None).Should().Be(0);
    }

    [Fact]
    public void Synchronous_HasValueOne()
    {
        ((int)CustomLogicBypass.Synchronous).Should().Be(1);
    }

    [Fact]
    public void Asynchronous_HasValueTwo()
    {
        ((int)CustomLogicBypass.Asynchronous).Should().Be(2);
    }

    [Fact]
    public void All_IsCombinationOfSyncAndAsync()
    {
        // All should be the bitwise OR of Synchronous and Asynchronous
        var expected = CustomLogicBypass.Synchronous | CustomLogicBypass.Asynchronous;
        CustomLogicBypass.All.Should().Be(expected);
    }

    [Fact]
    public void All_HasValueThree()
    {
        ((int)CustomLogicBypass.All).Should().Be(3);
    }

    #endregion

    #region Flags Behavior Tests

    [Fact]
    public void IsFlagsEnum()
    {
        var type = typeof(CustomLogicBypass);
        type.IsDefined(typeof(FlagsAttribute), false).Should().BeTrue();
    }

    [Fact]
    public void All_ContainsSynchronous()
    {
        (CustomLogicBypass.All & CustomLogicBypass.Synchronous).Should().Be(CustomLogicBypass.Synchronous);
        CustomLogicBypass.All.HasFlag(CustomLogicBypass.Synchronous).Should().BeTrue();
    }

    [Fact]
    public void All_ContainsAsynchronous()
    {
        (CustomLogicBypass.All & CustomLogicBypass.Asynchronous).Should().Be(CustomLogicBypass.Asynchronous);
        CustomLogicBypass.All.HasFlag(CustomLogicBypass.Asynchronous).Should().BeTrue();
    }

    [Fact]
    public void Synchronous_DoesNotContainAsynchronous()
    {
        CustomLogicBypass.Synchronous.HasFlag(CustomLogicBypass.Asynchronous).Should().BeFalse();
    }

    [Fact]
    public void Asynchronous_DoesNotContainSynchronous()
    {
        CustomLogicBypass.Asynchronous.HasFlag(CustomLogicBypass.Synchronous).Should().BeFalse();
    }

    [Fact]
    public void CanCombineFlags()
    {
        var combined = CustomLogicBypass.Synchronous | CustomLogicBypass.Asynchronous;
        combined.Should().Be(CustomLogicBypass.All);
    }

    #endregion

    #region String Representation Tests

    [Fact]
    public void None_ToStringReturnsNone()
    {
        CustomLogicBypass.None.ToString().Should().Be("None");
    }

    [Fact]
    public void Synchronous_ToStringReturnsSynchronous()
    {
        CustomLogicBypass.Synchronous.ToString().Should().Be("Synchronous");
    }

    [Fact]
    public void Asynchronous_ToStringReturnsAsynchronous()
    {
        CustomLogicBypass.Asynchronous.ToString().Should().Be("Asynchronous");
    }

    [Fact]
    public void All_ToStringReturnsAll()
    {
        CustomLogicBypass.All.ToString().Should().Be("All");
    }

    #endregion
}
