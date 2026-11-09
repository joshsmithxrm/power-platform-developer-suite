using System.Text.Json;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Resilience;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

// Common BEFORE/fixed contract: unchanged LF product plus the same strict synthetic harness.
[Trait("Category", "Unit")]
public sealed class ColumnLabelFailureClassificationTests(ITestOutputHelper output)
{
    private const string SecretMarker = "SyntheticColumnLabelSecret";

    [Theory]
    [InlineData("state", "RetrieveAttribute", "auth")]
    [InlineData("state", "RetrieveAttribute", "timeout")]
    [InlineData("guid-primary", "RetrieveEntity", "auth")]
    [InlineData("guid-primary", "RetrieveEntity", "timeout")]
    [InlineData("state", "UpdateAttribute", "auth")]
    [InlineData("state", "UpdateAttribute", "timeout")]
    [InlineData("state", "PublishXml", "auth")]
    [InlineData("state", "PublishXml", "timeout")]
    [InlineData("string", "RetrieveAttribute", "auth")]
    [InlineData("string", "RetrieveAttribute", "timeout")]
    [InlineData("legacy-primary", "RetrieveAttribute", "auth")]
    [InlineData("legacy-primary", "RetrieveAttribute", "timeout")]
    [Trait("AC", "AC-127")]
    public async Task RawSdkFailures_RetainClassificationAndPrivateOutput(string shape, string operation, string failure)
    {
        using var h = new ColumnLabelHarness(shape, output);
        // Same actually compiled constructor as MA3's 20-case read-failure proof.
        Exception raw = failure == "auth"
            ? new DataverseAuthenticationException("ClientSecret=" + SecretMarker, requiresReauthentication: true)
            : new TimeoutException("ClientSecret=" + SecretMarker);
        h.FaultOperation = operation;
        h.Fault = raw;
        var expectedCode = failure == "auth" ? ErrorCodes.Auth.Expired : ErrorCodes.Connection.Timeout;
        var expectedExit = failure == "auth" ? ExitCodes.AuthError : ExitCodes.ConnectionError;
        string[] expectedOperations = operation switch
        {
            "RetrieveAttribute" => ["RetrieveAttribute"],
            "RetrieveEntity" => ["RetrieveAttribute", "RetrieveEntity"],
            "UpdateAttribute" => ["RetrieveAttribute", "UpdateAttribute"],
            "PublishXml" => ["RetrieveAttribute", "UpdateAttribute", "PublishXml"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        PpdsException? error = null;
        var reachedRequestedFailure = false;
        string? publicErrorJson = null;
        try
        {
            error = Assert.IsAssignableFrom<PpdsException>(await Record.ExceptionAsync(() => h.UpdateAsync(new UpdateColumnRequest
            {
                EntityLogicalName = ColumnLabelHarness.Table,
                ColumnLogicalName = ColumnLabelHarness.Column,
                SolutionUniqueName = "FixtureSolution",
                DisplayName = "Requested label",
                Description = "Requested description",
                Publish = true
            })));
            // Prove the actual raw throw site, identity and acknowledged stage before
            // testing classification, so fixture/setup errors cannot be a valid BEFORE.
            Assert.Equal(expectedOperations, h.Requests.Select(request => request.RequestName));
            Assert.Same(raw, error.InnerException);
            Assert.Equal(operation == "PublishXml" ? 1 : 0, h.CacheInvalidations);
            publicErrorJson = JsonSerializer.Serialize(ExceptionMapper.Map(error));
            Assert.DoesNotContain(SecretMarker, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, error.UserMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, publicErrorJson, StringComparison.Ordinal);
            reachedRequestedFailure = true;

            Assert.Equal(expectedCode, error.ErrorCode);
            Assert.Equal(expectedCode, ExceptionMapper.Map(error).Code);
            Assert.Equal(expectedExit, ExceptionMapper.ToExitCode(error));
            if (failure == "auth")
                Assert.True(Assert.IsType<PpdsAuthException>(error).RequiresReauthentication);
            else
                Assert.False(error is PpdsAuthException);
        }
        finally
        {
            output.WriteLine("PPDS_LABEL_FAILURE_PROOF=" + JsonSerializer.Serialize(new
            {
                shape, operation, failure, reachedRequestedFailure,
                expectedOperations, actualOperations = h.Requests.Select(request => request.RequestName),
                expectedCode, expectedExit,
                actualCode = error?.ErrorCode,
                actualExit = error == null ? (int?)null : ExceptionMapper.ToExitCode(error),
                originalCausePreserved = ReferenceEquals(raw, error?.InnerException),
                requiresReauthentication = error is PpdsAuthException { RequiresReauthentication: true },
                h.CacheInvalidations, publicErrorJson,
                onlySyntheticHarness = true, realProviderOrAuth = false
            }));
        }
    }
}
