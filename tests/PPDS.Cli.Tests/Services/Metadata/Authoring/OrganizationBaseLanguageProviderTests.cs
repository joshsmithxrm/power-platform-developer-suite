using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// The base language every label is written in: <c>organization.languagecode</c>, read once per environment on its
/// own lease, cached only after a successful read, with classified failures (specs/metadata-authoring.md "Label
/// languages", AC-142).
/// </summary>
[Trait("Category", "Unit")]
public sealed class OrganizationBaseLanguageProviderTests
{
    [Fact]
    public async Task ReadsOrganizationLanguageCode_OnceAndCaches()
    {
        var f = new Fixture { Reply = _ => Organization(1036) };
        Assert.Equal(1036, await f.Provider.GetBaseLanguageAsync());
        Assert.Equal(1036, await f.Provider.GetBaseLanguageAsync());
        var query = Assert.IsType<QueryExpression>(Assert.Single(f.Requests).Query);
        Assert.Equal("organization", query.EntityName);
        Assert.Equal(new[] { "languagecode" }, query.ColumnSet.Columns);
        Assert.Equal(1, f.Checkouts);
        Assert.Equal(0, f.Open);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneRead()
    {
        var gate = new TaskCompletionSource();
        var f = new Fixture { ReplyAsync = async _ => { await gate.Task; return Organization(1033); } };
        var calls = Enumerable.Range(0, 5).Select(_ => f.Provider.GetBaseLanguageAsync()).ToArray();
        gate.SetResult();
        Assert.All(await Task.WhenAll(calls), lcid => Assert.Equal(1033, lcid));
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task AFailedRead_IsNotCached()
    {
        var attempt = 0;
        var f = new Fixture { Reply = _ => ++attempt == 1 ? throw new TimeoutException("synthetic") : Organization(1036) };
        await Assert.ThrowsAsync<PpdsException>(() => f.Provider.GetBaseLanguageAsync());
        Assert.Equal(1036, await f.Provider.GetBaseLanguageAsync());
        Assert.Equal(2, f.Requests.Count);
        Assert.Equal(0, f.Open);
    }

    public static TheoryData<string> Responses => new() { "no-rows", "no-value", "zero", "wrong-type" };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task AResponseWithoutALanguage_IsSdkOperationFailed(string response)
    {
        var f = new Fixture
        {
            Reply = _ => response switch
            {
                "no-rows" => new RetrieveMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection() } },
                "no-value" => new RetrieveMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection(new List<Entity> { new("organization") }) } },
                "zero" => Organization(0),
                _ => new OrganizationResponse()
            }
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Provider.GetBaseLanguageAsync());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, error.ErrorCode);
        Assert.Contains("organization.languagecode", error.Message);
    }

    [Fact]
    public async Task AnExpiredSignIn_IsAnAuthErrorWithTheReauthenticationSignal()
    {
        var cause = new DataverseAuthenticationException("synthetic", requiresReauthentication: true);
        var f = new Fixture { Reply = _ => throw cause };
        var error = await Assert.ThrowsAsync<PpdsAuthException>(() => f.Provider.GetBaseLanguageAsync());
        Assert.True(error.RequiresReauthentication);
        Assert.Same(cause, error.InnerException);
        Assert.Equal(ExitCodes.AuthError, ExceptionMapper.MapWithExitCode(error).ExitCode);
    }

    [Fact]
    public async Task AConnectionFailure_KeepsItsConnectionCode()
    {
        var f = new Fixture { Reply = _ => throw new TimeoutException("synthetic") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Provider.GetBaseLanguageAsync());
        Assert.NotEqual(MetadataErrorCodes.SdkOperationFailed, error.ErrorCode);
        Assert.Equal(ExitCodes.ConnectionError, ExceptionMapper.MapWithExitCode(error).ExitCode);
        Assert.DoesNotContain("synthetic", error.Message);
    }

    [Fact]
    public async Task AnyOtherFailure_IsSdkOperationFailed_WithoutProviderText()
    {
        var f = new Fixture { Reply = _ => throw new InvalidOperationException("secret provider text") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Provider.GetBaseLanguageAsync());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, error.ErrorCode);
        Assert.DoesNotContain("secret provider text", error.Message);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var f = new Fixture { Reply = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Provider.GetBaseLanguageAsync(cts.Token));
        Assert.Equal(0, f.Open);
    }

    [Fact]
    public void Registration_GivesOneProviderPerServiceProvider_SharedByEveryAuthoringService()
    {
        static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<PPDS.Cli.Infrastructure.Safety.IShakedownGuard, PPDS.Cli.Tests.Services.Shared.InactiveFakeShakedownGuard>();
            services.AddSingleton(new Mock<IDataverseConnectionPool>().Object);
            services.AddSingleton<SchemaValidator>(); // registered with the Dataverse services in production
            PPDS.Cli.Services.ServiceRegistration.AddCliApplicationServices(services);
            return services.BuildServiceProvider();
        }
        // The production registration is a singleton per service provider, and each environment gets its own service
        // provider and pool, so the cached language is never shared across environments.
        using var first = Build();
        Assert.NotNull(first.GetRequiredService<IMetadataAuthoringService>());
        using var second = Build();
        Assert.Same(first.GetRequiredService<IOrganizationBaseLanguageProvider>(), first.GetRequiredService<IOrganizationBaseLanguageProvider>());
        Assert.NotSame(first.GetRequiredService<IOrganizationBaseLanguageProvider>(), second.GetRequiredService<IOrganizationBaseLanguageProvider>());
    }

    [Fact]
    public void FixedLanguage_RejectsANonPositiveLcid()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FixedOrganizationBaseLanguage(0));

    private static RetrieveMultipleResponse Organization(int lcid)
    {
        var row = new Entity("organization") { ["languagecode"] = lcid };
        return new RetrieveMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection(new List<Entity> { row }) } };
    }

    private sealed class Fixture
    {
        public List<RetrieveMultipleRequest> Requests { get; } = [];
        public int Checkouts { get; private set; }
        public int Open { get; private set; }
        public Func<OrganizationRequest, OrganizationResponse>? Reply { get; init; }
        public Func<OrganizationRequest, Task<OrganizationResponse>>? ReplyAsync { get; init; }
        public OrganizationBaseLanguageProvider Provider { get; }

        public Fixture()
        {
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Checkouts++;
                    Open++;
                    var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Returns(() => { Open--; return ValueTask.CompletedTask; });
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                        {
                            Requests.Add(Assert.IsType<RetrieveMultipleRequest>(request));
                            return ReplyAsync != null ? ReplyAsync(request) : Task.FromResult(Reply!(request));
                        });
                    return client.Object;
                });
            Provider = new OrganizationBaseLanguageProvider(pool.Object);
        }
    }
}
