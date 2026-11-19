using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// #1759: over RPC an unknown global choice is <c>Operation.NotFound</c> (the extension used to get
/// <c>Operation.Internal</c> from the daemon's catch-all) for the two methods whose target is a global choice by name:
/// <c>metadata/deleteGlobalChoice</c> and <c>metadata/globalOptionSet</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GlobalChoiceNotFoundRpcTests
{
    private const string Url = "https://offline.invalid";
    private const string Missing = "mayo_Missing";

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task DeleteGlobalChoice_UnknownName_IsOperationNotFound()
    {
        using var fake = new GlobalChoiceServerFake();
        using var rpc = await RpcFixture.CreateAsync(authoring: fake.Service, query: null);

        var act = () => rpc.Handler.MetadataDeleteGlobalChoiceAsync("Sol", Missing);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StructuredErrorCode.Should().Be(ErrorCodes.Operation.NotFound);
        ex.Message.Should().Contain(Missing);
        fake.Requests.Should().NotContain(r => r is Microsoft.Xrm.Sdk.Messages.DeleteOptionSetRequest);
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task GlobalOptionSet_UnknownName_IsOperationNotFound()
    {
        var query = new Mock<IMetadataQueryService>(MockBehavior.Strict);
        query.Setup(q => q.GetOptionSetAsync(Missing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OptionSetNotFoundException(Missing));
        using var rpc = await RpcFixture.CreateAsync(authoring: null, query: query.Object);

        var act = () => rpc.Handler.MetadataGlobalOptionSetAsync(Missing);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StructuredErrorCode.Should().Be(ErrorCodes.Operation.NotFound);
        ex.Message.Should().Contain(Missing);
    }

    private sealed class RpcFixture : IDisposable
    {
        private readonly string _path;
        private readonly ServiceProvider _auth;
        private readonly ServiceProvider _services;
        internal RpcMethodHandler Handler { get; }

        private RpcFixture(string path, ProfileStore store, IMetadataAuthoringService? authoring, IMetadataQueryService? query)
        {
            _path = path;
            _auth = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            var services = new ServiceCollection();
            if (authoring != null) services.AddSingleton(authoring);
            if (query != null) services.AddSingleton(query);
            _services = services.BuildServiceProvider();
            var daemon = new Mock<IDaemonConnectionPoolManager>(MockBehavior.Strict);
            daemon.Setup(p => p.GetOrCreateServiceProviderAsync(It.IsAny<IReadOnlyList<string>>(), Url,
                It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(_services);
            Handler = new RpcMethodHandler(daemon.Object, _auth);
        }

        public static async Task<RpcFixture> CreateAsync(IMetadataAuthoringService? authoring, IMetadataQueryService? query)
        {
            var path = Path.Combine(Path.GetTempPath(), "ppds-rpc-choice-" + Guid.NewGuid().ToString("N") + ".json");
            var store = new ProfileStore(path);
            var profiles = await store.LoadAsync(); // The file is absent; this initializes only the cached collection.
            profiles.Add(new AuthProfile { Name = "offline", Environment = new PPDS.Auth.Profiles.EnvironmentInfo { Url = Url } }, setAsActive: true);
            return new RpcFixture(path, store, authoring, query);
        }

        public void Dispose()
        {
            Assert.False(File.Exists(_path));
            Handler.Dispose();
            _services.Dispose();
            _auth.Dispose();
        }
    }
}
