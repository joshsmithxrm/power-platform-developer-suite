using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.History;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

public sealed class RpcProfileIdentityTests
{
    private static AuthProfile Profile(int index = 1, string? name = "first") => new()
    {
        Index = index, Name = name, AuthMethod = AuthMethod.ClientSecret, Cloud = CloudEnvironment.Public,
        TenantId = "tenant", ApplicationId = "client", ObjectId = "principal",
        Username = "user", CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        Environment = new PPDS.Auth.Profiles.EnvironmentInfo { Url = "https://default.crm.dynamics.com", DisplayName = "Default" }
    };

    [Fact]
    public void Identity_SurvivesRenameReorderAndUsageChanges()
    {
        var profile = Profile();
        var identity = RpcProfileIdentity.Create(profile);
        profile.Name = "renamed";
        profile.Index = 42;
        profile.LastUsedAt = DateTimeOffset.UtcNow;
        profile.Environment!.Url = "https://other.crm.dynamics.com";
        Assert.Equal(identity, RpcProfileIdentity.Create(profile));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("client")]
    [InlineData("account")]
    [InlineData("username")]
    [InlineData("method")]
    [InlineData("creation")]
    [InlineData("homeAccount")]
    public void Identity_ChangesWhenAuthenticationIdentityChanges(string field)
    {
        var profile = Profile();
        var identity = RpcProfileIdentity.Create(profile);
        switch (field)
        {
            case "tenant": profile.TenantId = "changed"; break;
            case "client": profile.ApplicationId = "changed"; break;
            case "account": profile.ObjectId = "changed"; break;
            case "username": profile.Username = "changed"; break;
            case "method": profile.AuthMethod = AuthMethod.DeviceCode; break;
            case "creation": profile.CreatedAt = profile.CreatedAt.AddTicks(1); break;
            case "homeAccount": profile.HomeAccountId = "changed"; break;
        }
        Assert.NotEqual(identity, RpcProfileIdentity.Create(profile));
    }

    [Fact]
    public void ResolveSnapshot_UsesIdentityAfterRenameAndIndexReuse()
    {
        var profile = Profile(name: null);
        var identity = RpcProfileIdentity.Create(profile);
        profile.Index = 2;
        var replacement = Profile(1, null);
        replacement.ObjectId = "other-user";
        var collection = new ProfileCollection { Profiles = new() { replacement, profile } };
        var snapshot = RpcProfileIdentity.ResolveSnapshot(collection, identity);
        profile.ObjectId = "mutated-later";
        profile.Environment!.Url = "https://mutated.crm.dynamics.com";
        Assert.Equal("principal", snapshot.ObjectId);
        Assert.Equal(2, snapshot.Index);
        Assert.Equal("https://default.crm.dynamics.com", snapshot.Environment!.Url);
    }

    [Fact]
    public void ResolveSnapshot_RejectsRemovedOrChangedIdentity()
    {
        var profile = Profile();
        var identity = RpcProfileIdentity.Create(profile);
        profile.ObjectId = "different";
        var collection = new ProfileCollection { Profiles = new() { profile } };
        Assert.Equal(ErrorCodes.Auth.ProfileNotFound, Assert.Throws<RpcException>(
            () => RpcProfileIdentity.ResolveSnapshot(collection, identity)).StructuredErrorCode);
        collection.Profiles.Clear();
        Assert.Throws<RpcException>(() => RpcProfileIdentity.ResolveSnapshot(collection, identity));
    }

    [Fact]
    public void ResolveSnapshot_RejectsAmbiguousIdentity()
    {
        var profile = Profile();
        var duplicate = profile.Clone();
        duplicate.Index = 2;
        duplicate.Name = "duplicate";
        var collection = new ProfileCollection { Profiles = new() { profile, duplicate } };
        Assert.Equal(ErrorCodes.Validation.InvalidArguments, Assert.Throws<RpcException>(
            () => RpcProfileIdentity.ResolveSnapshot(collection, RpcProfileIdentity.Create(profile))).StructuredErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("first")]
    [InlineData("1")]
    [InlineData("not-a-valid-identity")]
    public void ResolveSnapshot_NeverFallsBackToNameOrIndex(string identity)
    {
        var collection = new ProfileCollection { Profiles = new() { Profile() } };
        Assert.Throws<RpcException>(() => RpcProfileIdentity.ResolveSnapshot(collection, identity));
    }

    [Fact]
    public async Task CapturedProvider_DoesNotReloadMutableProfileName_AndSeparatesIdentities()
    {
        var loads = 0;
        await using var manager = new DaemonConnectionPoolManager(loadProfilesAsync: _ =>
        {
            loads++;
            throw new InvalidOperationException("Captured profiles must not be looked up again.");
        });
        var profile = Profile();
        var first = await manager.GetOrCreateCapturedServiceProviderAsync(profile, "https://target.crm.dynamics.com");
        var again = await manager.GetOrCreateCapturedServiceProviderAsync(profile.Clone(), "https://target.crm.dynamics.com/");
        Assert.Same(first, again);
        profile.ObjectId = "different-principal";
        var different = await manager.GetOrCreateCapturedServiceProviderAsync(profile, "https://target.crm.dynamics.com");
        Assert.NotSame(first, different);
        Assert.Equal(0, loads);
        manager.InvalidateProfile("first");
        var invalidated = await manager.GetOrCreateCapturedServiceProviderAsync(profile, "https://target.crm.dynamics.com");
        Assert.NotSame(different, invalidated);
    }

    [Fact]
    public async Task QueryFetch_UsesCapturedIdentityAndRecordsExecutedEnvironment()
    {
        using var fixture = await Fixture.CreateAsync();
        var profile = fixture.Collection.ActiveProfile!;
        var identity = RpcProfileIdentity.Create(profile);
        profile.Name = "renamed";
        profile.Index = 2;
        fixture.Collection.ActiveProfileIndex = 2;
        var substitute = Profile(1, "first");
        substitute.ObjectId = "different-principal";
        fixture.Collection.Profiles.Add(substitute);
        var historySaved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.History.Setup(h => h.AddQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string url, string sql, int? count, long? duration, CancellationToken _) =>
            {
                historySaved.TrySetResult(url);
                return Task.FromResult(new QueryHistoryEntry { Id = "test", Sql = sql, ExecutedAt = DateTimeOffset.UtcNow });
            });
        await fixture.Handler.QueryFetchAsync(new QueryFetchRequest
        {
            FetchXml = "<fetch><entity name='account'/></fetch>",
            EnvironmentUrl = "https://explicit.crm.dynamics.com",
            ProfileName = "1",
            ProfileIdentity = identity
        });
        fixture.Pools.Verify(p => p.GetOrCreateCapturedServiceProviderAsync(
            It.Is<AuthProfile>(captured => captured.ObjectId == "principal" && captured.Index == 2),
            "https://explicit.crm.dynamics.com", It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Pools.Verify(p => p.GetOrCreateServiceProviderAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("https://explicit.crm.dynamics.com", await historySaved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QueryFetch_OmittedIdentityRetainsNameResolution()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Handler.QueryFetchAsync(new QueryFetchRequest
        {
            FetchXml = "<fetch><entity name='account'/></fetch>", ProfileName = "first"
        });
        fixture.Pools.Verify(p => p.GetOrCreateServiceProviderAsync(
            It.Is<IReadOnlyList<string>>(names => names.Count == 1 && names[0] == "first"),
            "https://default.crm.dynamics.com", It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Pools.Verify(p => p.GetOrCreateCapturedServiceProviderAsync(
            It.IsAny<AuthProfile>(), It.IsAny<string>(), It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("fetch")]
    [InlineData("complete")]
    [InlineData("explain")]
    public async Task QueryMethods_RejectMissingCapturedIdentityBeforePoolAccess(string method)
    {
        using var fixture = await Fixture.CreateAsync();
        var identity = RpcProfileIdentity.Create(fixture.Collection.ActiveProfile!);
        fixture.Collection.Profiles.Clear();
        Task invoke = method switch
        {
            "sql" => fixture.Handler.QuerySqlAsync(new QuerySqlRequest { Sql = "SELECT name FROM account", ProfileIdentity = identity }),
            "fetch" => fixture.Handler.QueryFetchAsync(new QueryFetchRequest { FetchXml = "<fetch/>", ProfileIdentity = identity }),
            "complete" => fixture.Handler.QueryCompleteAsync(new QueryCompleteRequest { Sql = "SELECT ", CursorOffset = 7, ProfileIdentity = identity }),
            _ => fixture.Handler.QueryExplainAsync(new QueryExplainRequest { Sql = "SELECT name FROM account", ProfileIdentity = identity })
        };
        var exception = await Assert.ThrowsAsync<RpcException>(() => invoke);
        Assert.Equal(ErrorCodes.Auth.ProfileNotFound, exception.StructuredErrorCode);
        Assert.Empty(fixture.Pools.Invocations);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _authServices;
        private readonly ServiceProvider _queryServices;
        public ProfileCollection Collection { get; }
        public Mock<IDaemonConnectionPoolManager> Pools { get; } = new();
        public Mock<IQueryHistoryService> History { get; } = new();
        public RpcMethodHandler Handler { get; }

        private Fixture(string directory, ProfileStore store, ProfileCollection collection)
        {
            _directory = directory;
            Collection = collection;
            var executor = new Mock<IQueryExecutor>();
            executor.Setup(e => e.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult { EntityLogicalName = "account", Columns = Array.Empty<QueryColumn>(), Records = Array.Empty<IReadOnlyDictionary<string, QueryValue>>(), Count = 0 });
            _queryServices = new ServiceCollection().AddSingleton(executor.Object).BuildServiceProvider();
            Pools.Setup(p => p.GetOrCreateServiceProviderAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_queryServices);
            Pools.Setup(p => p.GetOrCreateCapturedServiceProviderAsync(It.IsAny<AuthProfile>(), It.IsAny<string>(), It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_queryServices);
            _authServices = new ServiceCollection().AddSingleton(store).AddSingleton(History.Object).BuildServiceProvider();
            Handler = new RpcMethodHandler(Pools.Object, _authServices);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ppds-notebook-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
            var collection = await store.LoadAsync();
            collection.Add(Profile(), setAsActive: true);
            return new Fixture(directory, store, collection);
        }

        public void Dispose()
        {
            Handler.Dispose();
            _queryServices.Dispose();
            _authServices.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}

