using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Views;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Views;

public sealed class ViewConfirmationCoverageTests
{
    internal const string Target = "https://views.example.test";
    private const string Fetch = "<fetch><entity name='account'><attribute name='name'/></entity></fetch>";

    public static TheoryData<string> Writers => new()
    {
        "add-column", "remove-column", "update-column", "reorder-columns",
        "set-sort", "clear-sort", "set-filter", "clear-filter", "set-fetchxml"
    };

    private static Task Write(IViewService service, string writer, CancellationToken ct = default) => writer switch
    {
        "add-column" => service.AddColumnAsync("account", "View", [new("name")], publish: true, solution: "fixture", cancellationToken: ct),
        "remove-column" => service.RemoveColumnAsync("account", "View", "name", true, "fixture", cancellationToken: ct),
        "update-column" => service.UpdateColumnAsync("account", "View", "name", 200, true, "fixture", cancellationToken: ct),
        "reorder-columns" => service.ReorderColumnsAsync("account", "View", ["name"], true, "fixture", cancellationToken: ct),
        "set-sort" => service.SetSortAsync("account", "View", [new("name", false)], true, "fixture", cancellationToken: ct),
        "clear-sort" => service.ClearSortAsync("account", "View", true, "fixture", cancellationToken: ct),
        "set-filter" => service.SetFilterAsync("account", "View", "<filter/>", true, "fixture", cancellationToken: ct),
        "clear-filter" => service.ClearFilterAsync("account", "View", true, "fixture", cancellationToken: ct),
        "set-fetchxml" => service.SetFetchXmlAsync("account", "View", Fetch, true, "fixture", cancellationToken: ct),
        _ => throw new ArgumentOutOfRangeException(nameof(writer))
    };

    internal static EnvironmentConfirmationService DevelopmentConfirmation()
        => Confirmation(EnvironmentType.Development).Service;

    private static (EnvironmentConfirmationService Service, Mock<IEnvironmentConfigService> Config) Confirmation(
        EnvironmentType? type = EnvironmentType.Production, ProtectionLevel? protection = null,
        bool readOnly = false, EnvironmentAcknowledgementStore? store = null,
        string session = "session", string identity = "identity", string target = Target)
    {
        var config = new Mock<IEnvironmentConfigService>(MockBehavior.Strict);
        config.Setup(c => c.GetConfigAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(type is null ? null : new EnvironmentConfig { Type = type, Protection = protection });
        return (new(new EnvironmentConfirmationContext(session, identity, target, readOnly),
            config.Object, store ?? new()), config);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task EveryWriter_FailsClosedBeforeClientAcquisition(string writer)
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var legacy = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(), NullLogger<ViewService>.Instance);
        var missing = await Assert.ThrowsAsync<PpdsException>(() => Write(legacy, writer));
        Assert.Equal(EnvironmentConfirmationErrorCodes.Unresolved, missing.ErrorCode);
        var guarded = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, Confirmation().Service);
        var denied = await Assert.ThrowsAsync<PpdsException>(() => Write(guarded, writer));
        Assert.Equal(EnvironmentConfirmationErrorCodes.Required, denied.ErrorCode);
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task EveryWriter_ReadOnlyRejectsBeforeClientAcquisition(string writer)
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var confirmation = Confirmation(EnvironmentType.Development, readOnly: true);
        var service = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, confirmation.Service);
        // Includes clear-sort/clear-filter and identical FetchXML: no-op intent never bypasses read-only.
        var denied = await Assert.ThrowsAsync<PpdsException>(() => Write(service, writer));
        Assert.Equal(EnvironmentConfirmationErrorCodes.ReadOnly, denied.ErrorCode);
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
        confirmation.Config.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task EveryWriter_ShakedownAndCancellationStopBeforeClientAcquisition(string writer)
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
        guard.Setup(g => g.EnsureCanMutate("views." + writer))
            .Throws(new PpdsException(ErrorCodes.Safety.ShakedownActive, "shakedown"));
        var unusedConfirmation = new Mock<IEnvironmentConfirmationService>(MockBehavior.Strict);
        var service = new ViewService(pool.Object, metadata.Object, guard.Object,
            NullLogger<ViewService>.Instance, unusedConfirmation.Object);
        Assert.Equal(ErrorCodes.Safety.ShakedownActive,
            (await Assert.ThrowsAsync<PpdsException>(() => Write(service, writer))).ErrorCode);
        unusedConfirmation.VerifyNoOtherCalls();
        guard.VerifyAll();

        using var source = new CancellationTokenSource();
        source.Cancel();
        var confirmation = Confirmation(EnvironmentType.Development);
        service = new(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, confirmation.Service);
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(service, writer, source.Token));
        Assert.Equal(source.Token, cancelled.CancellationToken);
        confirmation.Config.VerifyNoOtherCalls();
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task EveryWriter_CancellationDuringPolicyReadStopsBeforeClientAcquisition(string writer)
    {
        using var source = new CancellationTokenSource();
        var confirmation = Confirmation(EnvironmentType.Development);
        confirmation.Config.Setup(c => c.GetConfigAsync(Target, source.Token))
            .Callback(source.Cancel).ReturnsAsync(new EnvironmentConfig { Type = EnvironmentType.Development });
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var service = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, confirmation.Service);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(service, writer, source.Token));
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(EnvironmentType.Production, null, false, false)]
    [InlineData(EnvironmentType.Production, null, true, true)]
    [InlineData(EnvironmentType.Unknown, null, false, false)]
    [InlineData(null, null, false, false)]
    [InlineData(EnvironmentType.Development, null, false, true)]
    [InlineData(EnvironmentType.Test, null, false, true)]
    [InlineData(EnvironmentType.Development, ProtectionLevel.Test, false, false)]
    [InlineData(EnvironmentType.Development, ProtectionLevel.Test, true, true)]
    [InlineData(EnvironmentType.Production, ProtectionLevel.Development, false, true)]
    public async Task RegisteredWriter_UsesSharedPolicy(EnvironmentType? type, ProtectionLevel? protection,
        bool acknowledge, bool allowed)
    {
        var fixture = new ReadWriteFixture();
        var config = Confirmation(type, protection).Config;
        var profile = new AuthProfile
        {
            Index = 1, Name = "captured", Username = "captured@example.test",
            Environment = new() { Url = "https://saved-default.example.test" }
        };
        var services = new ServiceCollection().AddLogging().AddCliApplicationServices()
            .AddSingleton(fixture.Pool.Object).AddSingleton(fixture.Metadata.Object)
            .AddSingleton(Mock.Of<IShakedownGuard>()).AddSingleton(config.Object)
            .AddSingleton(new ResolvedConnectionInfo { Profile = profile, EnvironmentUrl = Target });
        await using var provider = services.BuildServiceProvider();
        var confirmation = provider.GetRequiredService<IEnvironmentConfirmationService>();
        Assert.IsType<EnvironmentConfirmationService>(confirmation);
        var capturedIdentity = confirmation.Context.ProfileIdentity;
        Assert.Equal(EnvironmentConfirmationErrorCodes.TargetMismatch,
            (await Assert.ThrowsAsync<PpdsException>(() =>
                confirmation.EnsureConfirmedAsync("modify view", profile.Environment.Url))).ErrorCode);
        if (acknowledge) await confirmation.EnsureConfirmedAsync("modify view", Target);
        // Changes to the external default profile cannot change an already captured context.
        profile.Username = "another@example.test";
        Assert.Equal(capturedIdentity, confirmation.Context.ProfileIdentity);
        Assert.Equal(Target, confirmation.Context.EnvironmentUrl);
        var view = provider.GetRequiredService<IViewService>();
        if (allowed)
        {
            await Write(view, "clear-sort");
            Assert.Equal(new[] { "update", "solution", "publish" }, fixture.Mutations);
            fixture.Client.Verify(c => c.DisposeAsync(), Times.Once);
        }
        else
        {
            Assert.Equal(EnvironmentConfirmationErrorCodes.Required,
                (await Assert.ThrowsAsync<PpdsException>(() => Write(view, "clear-sort"))).ErrorCode);
            fixture.Pool.Verify(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()), Times.Never);
            Assert.Empty(fixture.Mutations);
        }
        config.Verify(c => c.GetConfigAsync(Target, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        config.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PolicyUnavailable_FailsClosedBeforeClientAcquisition()
    {
        var confirmation = Confirmation(EnvironmentType.Development);
        confirmation.Config.Setup(c => c.GetConfigAsync(Target, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("policy read failed"));
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var service = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, confirmation.Service);
        Assert.Equal(EnvironmentConfirmationErrorCodes.PolicyUnavailable,
            (await Assert.ThrowsAsync<PpdsException>(() => Write(service, "clear-sort"))).ErrorCode);
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("different-session", "identity", Target)]
    [InlineData("session", "different-identity", Target)]
    [InlineData("session", "identity", "https://other.example.test")]
    public async Task Acknowledgement_IsScopedAndPolicyRechecked(string session, string identity, string target)
    {
        var store = new EnvironmentAcknowledgementStore();
        var original = Confirmation(store: store);
        await original.Service.EnsureConfirmedAsync("modify view", Target);
        var other = Confirmation(store: store, session: session, identity: identity, target: target);
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var metadata = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
        var service = new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, other.Service);
        Assert.Equal(EnvironmentConfirmationErrorCodes.Required,
            (await Assert.ThrowsAsync<PpdsException>(() => Write(service, "clear-sort"))).ErrorCode);
        original.Config.Setup(c => c.GetConfigAsync(Target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentConfig { Type = EnvironmentType.Development, Protection = ProtectionLevel.Test });
        service = new(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(),
            NullLogger<ViewService>.Instance, original.Service);
        Assert.Equal(EnvironmentConfirmationErrorCodes.Required,
            (await Assert.ThrowsAsync<PpdsException>(() => Write(service, "clear-sort"))).ErrorCode);
        pool.VerifyNoOtherCalls();
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyConstructor_PreservesSignatureAndReadAccess()
    {
        Assert.NotNull(typeof(ViewService).GetConstructor([
            typeof(IDataverseConnectionPool), typeof(ICachedMetadataProvider),
            typeof(IShakedownGuard), typeof(ILogger<ViewService>)]));
        var fixture = new ReadWriteFixture();
        var service = new ViewService(fixture.Pool.Object, fixture.Metadata.Object,
            Mock.Of<IShakedownGuard>(), NullLogger<ViewService>.Instance);
        Assert.Single((await service.ListAsync("account")).Items);
        Assert.Equal("View", (await service.GetAsync("account", "View")).Name);
        Assert.Empty(fixture.Mutations);
        fixture.Client.Verify(c => c.DisposeAsync(), Times.Exactly(2));
    }

    private sealed class ReadWriteFixture
    {
        public Mock<IDataverseConnectionPool> Pool { get; } = new();
        public Mock<ICachedMetadataProvider> Metadata { get; } = new();
        public Mock<IPooledClient> Client { get; } = new();
        public List<string> Mutations { get; } = [];

        public ReadWriteFixture()
        {
            var state = new Entity("savedquery", Guid.NewGuid())
            {
                ["name"] = "View", ["fetchxml"] = Fetch, ["layoutxml"] = "<grid><row><cell name='name'/></row></grid>",
                ["returnedtypecode"] = "account", ["ismanaged"] = false
            };
            state["savedqueryid"] = state.Id;
            Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(Client.Object);
            Metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
                new EntitySummary { LogicalName = "account", SchemaName = "Account", DisplayName = "Account", ObjectTypeCode = 1 }
            ]);
            Client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection([state]));
            Client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((OrganizationRequest request, CancellationToken _) =>
                {
                    if (request is RetrieveUnpublishedMultipleRequest)
                        return new RetrieveUnpublishedMultipleResponse { Results = new() { ["EntityCollection"] = new EntityCollection([state]) } };
                    Mutations.Add(request is AddSolutionComponentRequest ? "solution" : "publish");
                    Assert.True(request is AddSolutionComponentRequest or PublishXmlRequest);
                    return new OrganizationResponse();
                });
            Client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((update, _) =>
                {
                    Mutations.Add("update");
                    foreach (var attribute in update.Attributes) state[attribute.Key] = attribute.Value;
                }).Returns(Task.CompletedTask);
        }
    }
}
