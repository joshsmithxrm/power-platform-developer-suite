using System.Text.Json;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Runs the real service acquisition path and actual structured-error helper.
/// This is a unit boundary check, not a protocol or live Dataverse test.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MetadataAcquisitionPrivacyTests
{
    [Theory, Trait("AC", "AC-102")]
    [InlineData("credential", ErrorCodes.Auth.InvalidCredentials, false)]
    [InlineData("custom-credential", "Synthetic.ProviderCode", false)]
    [InlineData("expired", ErrorCodes.Auth.Expired, true)]
    [InlineData("permission", ErrorCodes.Auth.InsufficientPermissions, false)]
    public async Task RawAuthDetail_StaysInCauseAndOutOfMcpPayload(string kind, string expectedCode, bool requiresReauthentication)
    {
        const string marker = "SYNTHETIC_PRIVATE_AUTHENTICATION_DETAIL";
        Exception original = kind switch
        {
            "credential" => new AuthenticationException(marker),
            "custom-credential" => new AuthenticationException(marker, expectedCode),
            _ => new DataverseAuthenticationException(marker, requiresReauthentication)
        };
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, CancellationToken.None)).ThrowsAsync(original);
        var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
        guard.Setup(g => g.EnsureCanMutate("metadata.option.add"));
        var service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), guard.Object);

        var error = await Assert.ThrowsAsync<PpdsAuthException>(() => service.AddOptionValueAsync(new AddOptionValueRequest
        {
            SolutionUniqueName = "FixtureSolution", OptionSetName = "fixture_choice", Label = "Fixture", Value = 77
        }));
        Assert.Same(original, error.InnerException);
        Assert.Equal(expectedCode, error.ErrorCode);
        Assert.Equal(ExitCodes.AuthError, ExceptionMapper.ToExitCode(error));
        Assert.Equal(requiresReauthentication, error.RequiresReauthentication);
        var actual = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(error));
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(marker, actual.Message);
        Assert.DoesNotContain(marker, error.UserMessage);
        Assert.Equal("Could not acquire a Dataverse connection.", payload.RootElement.GetProperty("userMessage").GetString());
        pool.Verify(p => p.GetClientAsync(null, null, CancellationToken.None), Times.Once);
        pool.VerifyNoOtherCalls();
        guard.Verify(g => g.EnsureCanMutate("metadata.option.add"), Times.Once);
        guard.VerifyNoOtherCalls();
    }
}
