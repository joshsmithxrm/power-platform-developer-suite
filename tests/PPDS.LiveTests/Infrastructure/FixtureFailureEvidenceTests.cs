using System.ServiceModel;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

public sealed class FixtureFailureEvidenceTests
{
    [Fact]
    public void DataverseFault_RecordsAuthoritativeRejectionWithoutArbitraryErrorDetails()
    {
        var activityId = Guid.NewGuid();
        var fault = new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040216), Message = "Invalid fixture reference", ActivityId = activityId };
        fault.ErrorDetails["secret"] = "must not be serialized";
        var evidence = FixtureFailureEvidence.Describe(new FaultException<OrganizationServiceFault>(fault));

        evidence.SubmissionOutcome.Should().Be("server-rejected");
        evidence.ServerCode.Should().Be("0x80040216");
        evidence.ServerMessage.Should().Be("Invalid fixture reference");
        evidence.ActivityId.Should().Be(activityId);
        System.Text.Json.JsonSerializer.Serialize(evidence).Should().NotContain("must not be serialized");
    }

    [Fact]
    public void TransportTimeoutAndCancellation_KeepSubmissionUnknown()
    {
        foreach (var exception in new Exception[] { new IOException("Disconnected"), new TimeoutException("Timed out"), new OperationCanceledException() })
        {
            var evidence = FixtureFailureEvidence.Describe(exception);
            evidence.SubmissionOutcome.Should().Be("unknown");
            evidence.ServerCode.Should().BeNull();
        }
    }

    [Fact]
    public void CleanupFailure_DoesNotReplacePrimaryExceptionOrItsOriginalStack()
    {
        var original = new InvalidOperationException("Original create failure");
        var evidence = new FixtureFailureEvidence();
        try { ThrowFromSubmission(original); }
        catch (Exception exception) { evidence.CapturePrimary(exception); }

        Action finish = () => evidence.ThrowIfFailed(["Cleanup also failed"], ["Create outcome unknown"]);
        var actual = finish.Should().Throw<InvalidOperationException>().Which;
        actual.Should().BeSameAs(original);
        actual.StackTrace.Should().Contain(nameof(ThrowFromSubmission));
        evidence.Primary!.Message.Should().Be("Original create failure");
    }

    [Fact]
    public void UnknownSubmissionWithoutPrimaryFailure_StillFailsValidation()
    {
        Action finish = () => new FixtureFailureEvidence().ThrowIfFailed([], ["Unconfirmed create"]);
        finish.Should().Throw<InvalidOperationException>().WithMessage("*Unconfirmed create*");
    }

    private static void ThrowFromSubmission(Exception exception) => throw exception;
}
