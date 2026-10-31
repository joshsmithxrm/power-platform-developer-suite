using FluentAssertions;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

public sealed class EnvironmentConfirmationServiceTests
{
    private const string Target = "https://org.crm.dynamics.com";

    private static (EnvironmentConfirmationService Service, Mock<IEnvironmentConfigService> Configuration) Create(
        EnvironmentType? type = EnvironmentType.Production, ProtectionLevel? protection = null,
        bool readOnly = false, EnvironmentAcknowledgementStore? store = null,
        string session = "session", string identity = "identity", string target = Target)
    {
        var config = new Mock<IEnvironmentConfigService>();
        config.Setup(x => x.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(type is null ? null : new EnvironmentConfig { Url = target, Type = type, Protection = protection });
        return (new EnvironmentConfirmationService(
            new EnvironmentConfirmationContext(session, identity, target, readOnly), config.Object,
            store ?? new EnvironmentAcknowledgementStore()), config);
    }

    [Theory]
    [InlineData(EnvironmentType.Production, null, true)]
    [InlineData(EnvironmentType.Unknown, null, true)]
    [InlineData(null, null, true)]
    [InlineData(EnvironmentType.Development, null, false)]
    [InlineData(EnvironmentType.Test, null, false)]
    [InlineData(EnvironmentType.Development, ProtectionLevel.Test, true)]
    [InlineData(EnvironmentType.Production, ProtectionLevel.Development, false)]
    public async Task PolicyMatrix(EnvironmentType? type, ProtectionLevel? protection, bool requiresConfirmation)
    {
        var (service, _) = Create(type, protection);
        var action = () => service.EnsureConfirmedAsync("write");
        if (requiresConfirmation)
            (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Required);
        else await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExactTarget_AcknowledgementIsReusedAndHarmlessUrlVariationsMatch()
    {
        var (service, _) = Create();
        await service.EnsureConfirmedAsync("write", "HTTPS://ORG.CRM.DYNAMICS.COM/");
        await service.EnsureConfirmedAsync("second write");
    }

    [Theory]
    [InlineData("https://other.crm.dynamics.com")]
    [InlineData("http://org.crm.dynamics.com")]
    public async Task Mismatch_IsRejectedEvenForDevelopment(string supplied)
    {
        var (service, _) = Create(EnvironmentType.Development);
        var action = () => service.EnsureConfirmedAsync("write", supplied);
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.TargetMismatch);
    }

    [Theory]
    [InlineData("https://user:password@org.crm.dynamics.com")]
    [InlineData("https://org.crm.dynamics.com?target=other")]
    [InlineData("https://org.crm.dynamics.com#fragment")]
    [InlineData("org")]
    [InlineData("")]
    public async Task InvalidAcknowledgement_IsRejected(string supplied)
    {
        var (service, _) = Create();
        var action = () => service.EnsureConfirmedAsync("write", supplied);
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.InvalidUrl);
    }

    [Fact]
    public async Task ReadOnly_CannotBeAcknowledgedAndDoesNotReadPolicy()
    {
        var (service, configuration) = Create(readOnly: true);
        var action = () => service.EnsureConfirmedAsync("write", Target);
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.ReadOnly);
        configuration.Verify(x => x.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingContext_FailsClosed()
    {
        var (service, _) = Create(session: "", identity: "", target: "");
        var action = () => service.EnsureConfirmedAsync("write");
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Unresolved);
    }

    [Theory]
    [InlineData("another-session", "identity", Target)]
    [InlineData("session", "another-identity", Target)]
    [InlineData("session", "identity", "https://other.crm.dynamics.com")]
    public async Task Acknowledgement_DoesNotCrossIdentitySessionOrTarget(string session, string identity, string target)
    {
        var store = new EnvironmentAcknowledgementStore();
        var (original, _) = Create(store: store);
        await original.EnsureConfirmedAsync("write", Target);
        var (other, _) = Create(store: store, session: session, identity: identity, target: target);
        var action = () => other.EnsureConfirmedAsync("write");
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Required);
    }

    [Fact]
    public async Task PolicyChange_InvalidatesPriorAcknowledgement()
    {
        var (service, configuration) = Create();
        await service.EnsureConfirmedAsync("write", Target);
        configuration.Setup(x => x.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentConfig { Type = EnvironmentType.Test, Protection = ProtectionLevel.Test });
        var action = () => service.EnsureConfirmedAsync("write");
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Required);
        await service.EnsureConfirmedAsync("write", Target);
    }

    [Fact]
    public async Task Cancellation_PassesToPolicyAndIsNotWrapped()
    {
        var (service, configuration) = Create();
        using var source = new CancellationTokenSource();
        configuration.Setup(x => x.GetConfigAsync(Target, source.Token))
            .ThrowsAsync(new OperationCanceledException(source.Token));
        var action = () => service.EnsureConfirmedAsync("write", Target, source.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        configuration.Verify(x => x.GetConfigAsync(Target, source.Token), Times.Once);
    }

    [Fact]
    public async Task ConcurrentAcknowledgements_DoNotAuthorizeAnotherTarget()
    {
        var store = new EnvironmentAcknowledgementStore();
        var (a, _) = Create(store: store);
        var (b, _) = Create(store: store, target: "https://other.crm.dynamics.com");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => a.EnsureConfirmedAsync("write", Target)));
        var action = () => b.EnsureConfirmedAsync("write");
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Required);
    }
}

