using FluentAssertions;
using PPDS.Cli.Commands.Serve.Handlers;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.DaemonTests;

/// <summary>
/// Integration tests that spawn the daemon process and communicate via JSON-RPC.
/// </summary>
[Collection("Daemon")]
[Trait("Category", "Integration")]
public class DaemonIntegrationTests : IClassFixture<DaemonTestFixture>
{
    private readonly DaemonTestFixture _fixture;

    public DaemonIntegrationTests(DaemonTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Daemon_IsRunning()
    {
        _fixture.IsRunning.Should().BeTrue("daemon process should be running");
    }

    [Fact]
    public async Task AuthList_ReturnsEmptyProfiles()
    {
        // Act - Call auth/list on empty profile store
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<AuthListResponse>(
            "auth/list",
            cancellationToken: CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Profiles.Should().NotBeNull();
        response.Profiles.Should().BeEmpty("no profiles in isolated config dir");
        response.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public async Task AuthWho_NoProfile_ReturnsError()
    {
        // Act & Assert - No active profile should throw
        var act = async () => await _fixture.Rpc.InvokeWithCancellationAsync<AuthWhoResponse>(
            "auth/who",
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<RemoteInvocationException>()
            .Where(ex => ex.Message.Contains("No active profile"));
    }

    [Fact]
    public async Task ProfilesInvalidate_ReturnsResponse()
    {
        // Act
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<ProfilesInvalidateResponse>(
            "profiles/invalidate",
            new object[] { "test-profile" },
            CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.ProfileName.Should().Be("test-profile");
        response.Invalidated.Should().BeTrue();
    }

    [Fact]
    public async Task ProfilesInvalidate_EmptyName_ReturnsError()
    {
        // Act & Assert
        var act = async () => await _fixture.Rpc.InvokeWithCancellationAsync<ProfilesInvalidateResponse>(
            "profiles/invalidate",
            new object[] { "" },
            CancellationToken.None);

        await act.Should().ThrowAsync<RemoteInvocationException>()
            .Where(ex => ex.Message.Contains("profileName") || ex.Message.Contains("required"));
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        // Act & Assert
        var act = async () => await _fixture.Rpc.InvokeWithCancellationAsync<object>(
            "unknown/method",
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<RemoteMethodNotFoundException>();
    }

    [Fact]
    public async Task AuthSelect_NoProfiles_ReturnsError()
    {
        // Act & Assert - No profiles exist to select
        var act = async () => await _fixture.Rpc.InvokeWithParameterObjectAsync<AuthSelectResponse>(
            "auth/select",
            new { index = 0 },
            CancellationToken.None);

        await act.Should().ThrowAsync<RemoteInvocationException>()
            .Where(ex => ex.Message.Contains("not found"));
    }

    [Fact]
    public async Task EnvList_NoProfile_ReturnsError()
    {
        // Act & Assert - Requires active profile
        var act = async () => await _fixture.Rpc.InvokeWithCancellationAsync<EnvListResponse>(
            "env/list",
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<RemoteInvocationException>()
            .Where(ex => ex.Message.Contains("No active profile"));
    }

    [Fact]
    public async Task SchemaList_ReturnsNotSupported()
    {
        // Act & Assert - Schema list is not yet implemented
        var act = async () => await _fixture.Rpc.InvokeWithParameterObjectAsync<SchemaListResponse>(
            "schema/list",
            new { entity = "account" },
            CancellationToken.None);

        await act.Should().ThrowAsync<RemoteInvocationException>()
            .Where(ex => ex.Message.Contains("NotSupported") || ex.Message.Contains("will be available"));
    }
}
