using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>The lock fault is recognised by its code only, never by its text.</summary>
[Trait("Category", "Unit")]
public class CustomizationLockRetryTests
{
    private static FaultException<OrganizationServiceFault> Fault(int code, OrganizationServiceFault? inner = null)
        => new(new OrganizationServiceFault { ErrorCode = code, Message = "any text", InnerFault = inner }, "any text");

    [Theory]
    [InlineData(unchecked((int)0x8004F063))]
    [InlineData(unchecked((int)0x80071151))]
    [InlineData(unchecked((int)0x8004F077))]
    [InlineData(unchecked((int)0x80090412))]
    public void LockFault_IsRecognisedByCode(int code)
        => Assert.Equal(code, CustomizationLockRetry.LockCode(Fault(code)));

    [Fact]
    public void LockFault_InAnInnerFaultOrInnerException_IsRecognised()
    {
        var inner = new OrganizationServiceFault { ErrorCode = unchecked((int)0x8004F061) };
        Assert.Equal(unchecked((int)0x8004F061), CustomizationLockRetry.LockCode(Fault(-1, inner)));
        Assert.Equal(unchecked((int)0x8004F064), CustomizationLockRetry.LockCode(new InvalidOperationException("wrapped", Fault(unchecked((int)0x8004F064)))));
    }

    [Fact]
    public void OtherFaults_AreNotLockFaults_EvenWithTheSameText()
    {
        Assert.Null(CustomizationLockRetry.LockCode(Fault(unchecked((int)0x80040217))));
        Assert.Null(CustomizationLockRetry.LockCode(new InvalidOperationException("Cannot start another [EntityCustomization] because there is a previous [EntityCustomization] running")));
    }

    [Fact]
    public async Task ANonLockFailure_PropagatesAtOnce()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CustomizationLockRetry.RunAsync(() =>
        {
            attempts++;
            throw new InvalidOperationException("other");
        }, "test", null));
        Assert.Equal(1, attempts);
    }
}
