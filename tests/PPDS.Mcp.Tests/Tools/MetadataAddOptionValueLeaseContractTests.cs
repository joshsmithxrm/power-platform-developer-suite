using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Compatibility controls through the actual option-add tool. The service is mocked;
/// these are tool-boundary tests, not MCP transport or Dataverse tests.
/// </summary>
[Trait("Category", "Unit")]
[Trait("AC", "AC-87")]
[Trait("AC", "AC-90")]
public sealed class MetadataAddOptionValueLeaseContractTests
{
    [Fact]
    public async Task RawPoolExhaustion_RemainsInternalAtMcpBoundary()
    {
        var timeout = new PoolExhaustedException(1, 1, TimeSpan.FromMilliseconds(40));
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromException<int>(timeout));

        var task = fixture.InvokeAsync();
        var actual = await Assert.ThrowsAsync<McpException>(() => task);

        Assert.True(task.IsFaulted);
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(ErrorCodes.Operation.Internal, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(timeout.Message, actual.Message);
        // Deliberately preserve the existing distinction from the CLI mapper. Changing
        // the raw acquisition/service contract is a separately pending decision.
        var (cliError, cliExit) = ExceptionMapper.MapWithExitCode(timeout);
        Assert.Equal(ErrorCodes.Connection.Timeout, cliError.Code);
        Assert.Equal(4, cliExit);
        fixture.AssertCalledOnce();
    }

    [Fact]
    public async Task WrappedSdkCancellation_RemainsCodedAndFaulted()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("SDK cancellation fixture", cancellation.Token);
        var wrapped = new PpdsException(MetadataErrorCodes.SdkOperationFailed, "SDK operation failed.", original);
        using var fixture = await Fixture.CreateAsync((_, _) =>
        {
            // Cancel only when the service is reached, after context/profile capture.
            cancellation.Cancel();
            return Task.FromException<int>(wrapped);
        });

        var task = fixture.InvokeAsync(cancellation.Token);
        var actual = await Assert.ThrowsAsync<McpException>(() => task);

        Assert.True(task.IsFaulted);
        Assert.False(task.IsCanceled);
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(wrapped.UserMessage, payload.RootElement.GetProperty("userMessage").GetString());
        Assert.Same(original, wrapped.InnerException);
        Assert.Equal(2, ExceptionMapper.ToExitCode(wrapped));
        fixture.AssertCalledOnce();
        Assert.Equal(cancellation.Token, fixture.Token);
    }

    [Fact]
    public async Task RawCancellation_PropagatesOriginalAndCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("acquisition cancellation fixture", cancellation.Token);
        using var fixture = await Fixture.CreateAsync((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<int>(original);
        });

        var task = fixture.InvokeAsync(cancellation.Token);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => task);

        Assert.Same(original, actual);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(task.IsCanceled);
        Assert.False(task.IsFaulted);
        var (cliError, cliExit) = ExceptionMapper.MapWithExitCode(actual);
        Assert.Equal(ErrorCodes.Operation.Cancelled, cliError.Code);
        Assert.Equal(2, cliExit);
        fixture.AssertCalledOnce();
        Assert.Equal(cancellation.Token, fixture.Token);
    }

    [Fact]
    public async Task AlreadyCodedFault_PreservesCodeAndContext()
    {
        var original = new PpdsException(ErrorCodes.Connection.Timeout, "A coded timeout.",
            new Dictionary<string, object> { ["scope"] = "fixture" });
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromException<int>(original));

        var task = fixture.InvokeAsync();
        var actual = await Assert.ThrowsAsync<McpException>(() => task);

        Assert.True(task.IsFaulted);
        using var payload = JsonDocument.Parse(actual.Message);
        Assert.Equal(original.ErrorCode, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(original.UserMessage, payload.RootElement.GetProperty("userMessage").GetString());
        Assert.Equal("fixture", payload.RootElement.GetProperty("context").GetProperty("scope").GetString());
        Assert.Equal(4, ExceptionMapper.ToExitCode(original));
        fixture.AssertCalledOnce();
    }

    [Fact]
    public async Task SuccessfulInvocation_ForwardsRequestOnce()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = await Fixture.CreateAsync((_, _) => Task.FromResult(77));

        var result = await fixture.InvokeAsync(cancellation.Token);

        Assert.Equal(77, result.Value);
        var request = Assert.IsType<AddOptionValueRequest>(fixture.Request);
        Assert.Equal("FixtureSolution", request.SolutionUniqueName);
        Assert.Equal("fixture_choice", request.OptionSetName);
        Assert.Equal("Changed", request.Label);
        Assert.Equal(77, request.Value);
        Assert.Equal("Description", request.Description);
        Assert.Equal("#123456", request.Color);
        Assert.Null(request.EntityLogicalName);
        Assert.Null(request.AttributeLogicalName);
        Assert.Equal(cancellation.Token, fixture.Token);
        fixture.AssertCalledOnce();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        private readonly string _directory;
        private readonly ProfileStore _store;
        private readonly McpToolContext _context;
        private readonly Mock<IMcpConnectionPoolManager> _pools = new(MockBehavior.Strict);
        private readonly Mock<ISecureCredentialStore> _credentials = new(MockBehavior.Strict);
        private readonly Mock<IMetadataAuthoringService> _service = new(MockBehavior.Strict);
        private int _providerCount;
        private int _serviceCount;
        private bool _disposed;
        private MetadataAddOptionValueTool Tool { get; }
        public AddOptionValueRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }

        private Fixture(Func<AddOptionValueRequest, CancellationToken, Task<int>> invoke)
        {
            _directory = Path.Combine(_tempRoot, "ppds-option-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _store = new ProfileStore(Path.Combine(_directory, "profiles.json"));
            _service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AddOptionValueRequest, CancellationToken>((request, token) =>
                {
                    _serviceCount++;
                    Request = request;
                    Token = token;
                    return invoke(request, token);
                });
            _context = new McpToolContext(_pools.Object, _store, _credentials.Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (connection, _, _, _) =>
                {
                    Assert.Equal("https://fixture.example.test", connection.EnvironmentUrl);
                    _providerCount++;
                    return new ServiceCollection().AddSingleton(_service.Object).BuildServiceProvider();
                }
            };
            Tool = new MetadataAddOptionValueTool(_context);
        }

        public static async Task<Fixture> CreateAsync(Func<AddOptionValueRequest, CancellationToken, Task<int>> invoke)
        {
            var fixture = new Fixture(invoke);
            try
            {
                await fixture._store.SaveAsync(new ProfileCollection
                {
                    ActiveProfileIndex = 1,
                    Profiles = [new AuthProfile
                    {
                        Index = 1, Name = "fixture",
                        Environment = new() { Url = "https://fixture.example.test" }
                    }]
                });
                await fixture._context.InitializeAsync();
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task<MetadataAddOptionValueResult> InvokeAsync(CancellationToken token = default)
            => Tool.ExecuteAsync("FixtureSolution", "fixture_choice", "Changed", 77,
                "Description", "#123456", token);

        public void AssertCalledOnce()
        {
            Assert.Equal(1, _providerCount);
            Assert.Equal(1, _serviceCount);
            _service.Verify(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Once);
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
            // Only the uniquely named child created by this fixture may be removed.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_directory)), _tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(_directory).StartsWith("ppds-option-contract-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
