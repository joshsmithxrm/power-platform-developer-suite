using System.Runtime.ExceptionServices;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Keeps fixture failures separate from recovery evidence and preserves the original stack.</summary>
internal sealed class FixtureFailureEvidence
{
    private ExceptionDispatchInfo? _primary;
    public FixtureFailure? Primary { get; private set; }

    public void CapturePrimary(Exception exception)
    {
        _primary = ExceptionDispatchInfo.Capture(exception);
        Primary = Describe(exception);
    }

    public void ThrowIfFailed(IReadOnlyCollection<string> cleanupFailures, IReadOnlyCollection<string> unknownSubmissions)
    {
        _primary?.Throw();
        if (cleanupFailures.Count != 0 || unknownSubmissions.Count != 0)
            throw new InvalidOperationException("Fixture recovery requires manual review: " +
                string.Join("; ", cleanupFailures.Concat(unknownSubmissions)));
    }

    public static FixtureFailure Describe(Exception exception)
    {
        // Only an explicit Dataverse fault acknowledges rejection. Timeouts, cancellation and
        // transport failures do not prove whether the server committed the submitted request.
        var fault = (exception as FaultException<OrganizationServiceFault>)?.Detail;
        return new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message,
            fault is null ? "unknown" : "server-rejected",
            fault is null ? null : $"0x{unchecked((uint)fault.ErrorCode):X8}", fault?.Message, fault?.ActivityId);
    }
}

internal sealed record FixtureFailure(string Type, string Message, string SubmissionOutcome,
    string? ServerCode, string? ServerMessage, Guid? ActivityId);
