using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Isolated synthetic probe through the real service, real tool and real JSON helper.
/// No SDK provider or MCP transport is started. Privacy assertions intentionally fail
/// on the uncorrected SDK-message path and must remain unchanged for any later fix.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MetadataSdkDiagnosticPrivacyProbeTests
{
    private const string SecretSentinel = "PPDS_SYNTHETIC_NOT_A_CREDENTIAL";
    private const string BearerSentinel = "PPDS_SYNTHETIC_NOT_A_TOKEN";

    [Theory, Trait("Probe", "PRIV-01/02/03/04")]
    [InlineData("global-color-read", "ClientSecret")]
    [InlineData("global-color-read", "Bearer")]
    [InlineData("global-color-write", "ClientSecret")]
    [InlineData("global-color-write", "Bearer")]
    public async Task ActualServiceColorFailure_UntrustedDetailMustNotReachMcp(string stage, string payloadKind)
    {
        using var cancellation = new CancellationTokenSource();
        var sdk = new SdkProbe(stage, Payload(payloadKind), cancellation.Token);
        using var fixture = await ToolFixture.CreateAsync(sdk.Service.AddOptionValueAsync);

        var task = fixture.InvokeAsync(cancellation.Token);
        var actual = await Assert.ThrowsAsync<McpException>(() => task);
        var wrapped = Assert.IsType<PpdsException>(fixture.ServiceError);
        Assert.Same(sdk.Original, wrapped.InnerException);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, wrapped.ErrorCode);
        Assert.Equal(2, ExceptionMapper.ToExitCode(wrapped));
        Assert.True(task.IsFaulted);
        sdk.AssertComplete();
        fixture.AssertCalledOnce(cancellation.Token);
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.False(payload.RootElement.TryGetProperty("success", out _));

        // Intended privacy contract: these same assertions must fail before and pass after a fix.
        var userMessage = payload.RootElement.GetProperty("userMessage").GetString()!;
        Assert.DoesNotContain(Sentinel(payloadKind), userMessage);
        Assert.DoesNotContain(Sentinel(payloadKind), actual.Message);
    }

    [Theory, Trait("Probe", "PRIV-05")]
    [InlineData("ClientSecret")]
    [InlineData("Bearer")]
    public async Task InitialInsertRawFailure_UsesGenericMcpError(string payloadKind)
    {
        using var cancellation = new CancellationTokenSource();
        var sdk = new SdkProbe("insert", Payload(payloadKind), cancellation.Token);
        using var fixture = await ToolFixture.CreateAsync(sdk.Service.AddOptionValueAsync);

        var task = fixture.InvokeAsync(cancellation.Token);
        var actual = await Assert.ThrowsAsync<McpException>(() => task);
        Assert.Same(sdk.Original, fixture.ServiceError);
        Assert.True(task.IsFaulted);
        sdk.AssertComplete();
        fixture.AssertCalledOnce(cancellation.Token);
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(ErrorCodes.Operation.Internal, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("An internal error occurred. See server logs for details.",
            payload.RootElement.GetProperty("userMessage").GetString());
        Assert.DoesNotContain(Sentinel(payloadKind), actual.Message);
    }

    [Theory, Trait("Probe", "PRIV-07")]
    [InlineData("global-color-read")]
    [InlineData("global-color-write")]
    public async Task ActualColorWrapper_CliMapperRemovesKnownSecretKey(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var sdk = new SdkProbe(stage, Payload("ClientSecret"), cancellation.Token);
        using var fixture = await ToolFixture.CreateAsync(sdk.Service.AddOptionValueAsync);

        await Assert.ThrowsAsync<McpException>(() => fixture.InvokeAsync(cancellation.Token));
        var wrapped = Assert.IsType<PpdsException>(fixture.ServiceError);
        Assert.Same(sdk.Original, wrapped.InnerException);
        var mapped = ExceptionMapper.MapWithExitCode(wrapped);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, mapped.Error.Code);
        Assert.Equal(2, mapped.ExitCode);
        Assert.DoesNotContain(SecretSentinel, mapped.Error.Message);
        sdk.AssertComplete();
        fixture.AssertCalledOnce(cancellation.Token);
    }

    [Fact, Trait("Probe", "PRIV-07")]
    public void DirectMapperCharacterization_DoesNotClaimBearerRedaction()
    {
        // Helper-only characterization, independent of a future service-owned fixed message.
        var key = ExceptionMapper.Map(new PpdsException("Synthetic.Code", Payload("ClientSecret")));
        Assert.DoesNotContain(SecretSentinel, key.Message);
        Assert.Contains("***REDACTED***", key.Message);
        var bearer = ExceptionMapper.Map(new PpdsException("Synthetic.Code", Payload("Bearer")));
        Assert.Contains(BearerSentinel, bearer.Message);
    }

    private static string Payload(string kind) => kind switch
    {
        "ClientSecret" => "ClientSecret=" + SecretSentinel + "; synthetic SDK diagnostic",
        "Bearer" => "Bearer " + BearerSentinel,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string Sentinel(string kind) => kind == "ClientSecret" ? SecretSentinel : BearerSentinel;

    private sealed class SdkProbe
    {
        private readonly string _stage;
        private readonly CancellationToken _token;
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<IShakedownGuard> _guard = new(MockBehavior.Strict);
        private readonly List<Mock<IPooledClient>> _leases = [];
        private readonly List<string> _calls = [];
        private int _disposed;
        public InvalidOperationException Original { get; }
        public DataverseMetadataAuthoringService Service { get; }

        public SdkProbe(string stage, string message, CancellationToken token)
        {
            _stage = stage;
            _token = token;
            Original = new InvalidOperationException(message);
            _guard.Setup(g => g.EnsureCanMutate("metadata.option.add"));
            _pool.Setup(p => p.GetClientAsync(null, null, token)).ReturnsAsync(() =>
            {
                var lease = new Mock<IPooledClient>(MockBehavior.Strict);
                _leases.Add(lease);
                lease.Setup(c => c.DisposeAsync()).Returns(() =>
                {
                    _disposed++;
                    return ValueTask.CompletedTask;
                });
                lease.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), token))
                    .Returns<OrganizationRequest, CancellationToken>((request, receivedToken) =>
                    {
                        Assert.Equal(token, receivedToken);
                        var current = request switch
                        {
                            InsertOptionValueRequest => "insert",
                            RetrieveOptionSetRequest => "global-color-read",
                            UpdateOptionSetRequest => "global-color-write",
                            _ => throw new InvalidOperationException("Unexpected synthetic SDK request")
                        };
                        _calls.Add(current);
                        if (current == stage) return Task.FromException<OrganizationResponse>(Original);
                        if (request is InsertOptionValueRequest)
                            return Task.FromResult<OrganizationResponse>(new InsertOptionValueResponse
                            {
                                Results = new ParameterCollection { ["NewOptionValue"] = 77 }
                            });
                        Assert.IsType<RetrieveOptionSetRequest>(request);
                        var options = new OptionSetMetadata { Name = "fixture_choice" };
                        options.Options.Add(new OptionMetadata(new Label("Changed", 1033), 77));
                        return Task.FromResult<OrganizationResponse>(new RetrieveOptionSetResponse
                        {
                            Results = new ParameterCollection { ["OptionSetMetadata"] = options }
                        });
                    });
                return lease.Object;
            });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), _guard.Object);
        }

        public void AssertComplete()
        {
            string[] expected = _stage switch
            {
                "insert" => ["insert"],
                "global-color-read" => ["insert", "global-color-read"],
                "global-color-write" => ["insert", "global-color-read", "global-color-write"],
                _ => throw new InvalidOperationException("Unexpected synthetic stage")
            };
            Assert.Equal(expected, _calls);
            Assert.Equal(expected.Length, _leases.Count);
            Assert.Equal(expected.Length, _disposed);
            _pool.Verify(p => p.GetClientAsync(null, null, _token), Times.Exactly(expected.Length));
            _pool.VerifyNoOtherCalls();
            _guard.Verify(g => g.EnsureCanMutate("metadata.option.add"), Times.Once);
            _guard.VerifyNoOtherCalls();
            foreach (var lease in _leases)
            {
                lease.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), _token), Times.Once);
                lease.Verify(c => c.DisposeAsync(), Times.Once);
                lease.VerifyNoOtherCalls();
            }
        }
    }

    private sealed class ToolFixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        private readonly string _directory;
        private readonly ProfileStore _store;
        private readonly McpToolContext _context;
        private readonly Mock<IMcpConnectionPoolManager> _pools = new(MockBehavior.Strict);
        private readonly Mock<ISecureCredentialStore> _credentials = new(MockBehavior.Strict);
        private readonly Mock<IMetadataAuthoringService> _service = new(MockBehavior.Strict);
        private readonly MetadataAddOptionValueTool _tool;
        private int _scopeCount;
        private int _serviceCount;
        private CancellationToken _token;
        private bool _disposed;
        public Exception? ServiceError { get; private set; }

        private ToolFixture(Func<AddOptionValueRequest, CancellationToken, Task<int>> invoke)
        {
            _directory = Path.Combine(_tempRoot, "ppds-sdk-privacy-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(_directory) || File.Exists(_directory))
                throw new InvalidOperationException("Refuse existing synthetic fixture directory.");
            Directory.CreateDirectory(_directory);
            _store = new ProfileStore(Path.Combine(_directory, "profiles.json"));
            _service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AddOptionValueRequest, CancellationToken>(async (request, token) =>
                {
                    _serviceCount++;
                    _token = token;
                    Assert.Equal("fixture_choice", request.OptionSetName);
                    Assert.Equal("#123456", request.Color);
                    Assert.Equal(77, request.Value);
                    Assert.Null(request.EntityLogicalName);
                    Assert.Null(request.AttributeLogicalName);
                    try { return await invoke(request, token); }
                    catch (Exception ex) { ServiceError = ex; throw; }
                });
            _context = new McpToolContext(_pools.Object, _store, _credentials.Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (connection, _, _, _) =>
                {
                    Assert.Equal("https://fixture.example.test", connection.EnvironmentUrl);
                    _scopeCount++;
                    return new ServiceCollection().AddSingleton(_service.Object).BuildServiceProvider();
                }
            };
            _tool = new MetadataAddOptionValueTool(_context);
        }

        public static async Task<ToolFixture> CreateAsync(Func<AddOptionValueRequest, CancellationToken, Task<int>> invoke)
        {
            var fixture = new ToolFixture(invoke);
            try
            {
                await fixture._store.SaveAsync(new ProfileCollection
                {
                    ActiveProfileIndex = 1,
                    Profiles = [new AuthProfile
                    {
                        Index = 1, Name = "synthetic-privacy-fixture",
                        Environment = new() { Url = "https://fixture.example.test" }
                    }]
                });
                await fixture._context.InitializeAsync();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public Task<MetadataAddOptionValueResult> InvokeAsync(CancellationToken token)
            => _tool.ExecuteAsync("FixtureSolution", "fixture_choice", "Changed", 77,
                "Description", "#123456", token);

        public void AssertCalledOnce(CancellationToken token)
        {
            Assert.Equal(1, _scopeCount);
            Assert.Equal(1, _serviceCount);
            Assert.Equal(token, _token);
            _service.Verify(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), token), Times.Once);
            _service.VerifyNoOtherCalls();
            _pools.VerifyNoOtherCalls();
            _credentials.VerifyNoOtherCalls();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _context.Dispose();
            _store.Dispose();
            var resolved = Path.GetFullPath(_directory);
            if (!string.Equals(Path.GetDirectoryName(resolved), _tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("ppds-sdk-privacy-", StringComparison.Ordinal)
                || (Directory.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Unexpected synthetic fixture cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
