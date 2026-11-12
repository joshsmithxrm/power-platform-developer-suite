using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PackageDownloadTests
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly Guid _packageId = Guid.NewGuid();
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly List<Mock<IPooledClient>> _leases = [];
    private readonly List<OrganizationRequest> _requests = [];
    private readonly List<int> _requestLeases = [];
    private readonly List<CancellationToken> _tokens = [];
    private readonly Dictionary<string, object> _fileColumns = [];
    private readonly OrganizationResponse _initialized = Response(
        ("FileContinuationToken", "opaque-token"), ("FileSizeInBytes", 3L), ("IsChunkingSupported", true));
    private readonly PluginRegistrationService _service;
    private string? _legacyContent;
    private byte[] _fileBytes = [1, 2, 3];
    private int _retrieves;
    private Guid? _returnedFileId;
    private Func<OrganizationRequest, OrganizationResponse>? _executeOverride;

    public PackageDownloadTests()
    {
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var leaseNumber = _leases.Count;
                var client = new Mock<IPooledClient>(MockBehavior.Strict);
                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                client.Setup(c => c.RetrieveAsync(It.IsAny<string>(), It.IsAny<Guid>(),
                        It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string table, Guid id, ColumnSet columns, CancellationToken token) =>
                    {
                        Assert.Equal(PluginPackage.EntityLogicalName, table);
                        Assert.Equal(_packageId, id);
                        _tokens.Add(token);
                        _retrieves++;
                        var entity = new Entity(table, columns.Columns.Contains(PluginPackage.Fields.Content)
                            ? id : _returnedFileId ?? id);
                        if (columns.Columns.Contains(PluginPackage.Fields.Content))
                        {
                            Assert.Equal(new[] { "content", "name", "version" }, columns.Columns.Order().ToArray());
                            entity[PluginPackage.Fields.Name] = "TestPackage";
                            entity[PluginPackage.Fields.Version] = "1.2.3";
                            entity[PluginPackage.Fields.Content] = _legacyContent;
                        }
                        else
                        {
                            Assert.Equal(new[] { "fileid", "package" }, columns.Columns.Order().ToArray());
                            foreach (var column in _fileColumns) entity[column.Key] = column.Value;
                        }
                        return entity;
                    });
                client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((OrganizationRequest request, CancellationToken token) =>
                    {
                        _tokens.Add(token);
                        _requests.Add(request);
                        _requestLeases.Add(leaseNumber);
                        if (_executeOverride != null) return _executeOverride(request);
                        return DefaultExecute(request);
                    });
                _leases.Add(client);
                return client.Object;
            });
        _service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(),
            Mock.Of<ILogger<PluginRegistrationService>>());
    }

    private OrganizationResponse DefaultExecute(OrganizationRequest request)
    {
        if (request.RequestName == "InitializeFileBlocksDownload") return _initialized;
        Assert.Equal("DownloadBlock", request.RequestName);
        Assert.Equal("opaque-token", request["FileContinuationToken"]);
        var offset = Assert.IsType<long>(request["Offset"]);
        var length = Assert.IsType<long>(request["BlockLength"]);
        return Response(("Data", _fileBytes.AsSpan((int)offset, (int)length).ToArray()));
    }

    private static OrganizationResponse Response(params (string Key, object Value)[] values)
    {
        var response = new OrganizationResponse();
        foreach (var (key, value) in values) response.Results[key] = value;
        return response;
    }

    private void HasFile(string column = "package", int size = 3)
    {
        _fileColumns[column] = Guid.NewGuid();
        _fileBytes = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        _initialized.Results["FileSizeInBytes"] = (long)size;
    }

    private void AssertLeasesDisposed()
    {
        Assert.Equal(_retrieves + (_requests.Count > 0 ? 1 : 0), _leases.Count);
        // All requests sharing a continuation token use one lease/principal.
        if (_requests.Count > 0) Assert.Single(_requestLeases.Distinct());
        foreach (var lease in _leases) lease.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task LegacyContent_PreservesFilenameWithoutFileRequests()
    {
        _legacyContent = Convert.ToBase64String(_fileBytes);
        var result = await _service.DownloadPackageAsync(_packageId);
        Assert.Equal(_fileBytes, result.Content);
        Assert.Equal("TestPackage.1.2.3.nupkg", result.FileName);
        Assert.Equal(1, _retrieves);
        Assert.Empty(_requests);
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData("package")]
    [InlineData("fileid")]
    public async Task FileColumn_UsesExactPackageAndObservedColumn(string column)
    {
        HasFile(column);
        _fileColumns[column == "package" ? "fileid" : "package"] = null!;
        using var cancellation = new CancellationTokenSource();
        var result = await _service.DownloadPackageAsync(_packageId, cancellation.Token);
        Assert.Equal(_fileBytes, result.Content);
        Assert.Equal("TestPackage.1.2.3.nupkg", result.FileName);
        var init = _requests[0];
        Assert.Equal("InitializeFileBlocksDownload", init.RequestName);
        var target = Assert.IsType<EntityReference>(init["Target"]);
        Assert.Equal(PluginPackage.EntityLogicalName, target.LogicalName);
        Assert.Equal(_packageId, target.Id);
        Assert.Equal(column, init["FileAttributeName"]);
        Assert.Equal(new[] { "InitializeFileBlocksDownload", "DownloadBlock" }, _requests.Select(r => r.RequestName));
        Assert.All(_tokens, token => Assert.Equal(cancellation.Token, token));
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFileColumns_PreservesNoContent(bool explicitNulls)
    {
        if (explicitNulls)
        {
            _fileColumns["package"] = null!;
            _fileColumns["fileid"] = null!;
        }
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.NoContent, error.ErrorCode);
        Assert.Empty(_requests);
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData("both")]
    [InlineData("same")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("foreign-record")]
    public async Task AmbiguousOrInvalidFileColumns_StopBeforeDownload(string problem)
    {
        HasFile();
        if (problem == "both") _fileColumns["fileid"] = Guid.NewGuid();
        if (problem == "same") _fileColumns["fileid"] = _fileColumns["package"];
        if (problem == "empty") _fileColumns["package"] = Guid.Empty;
        if (problem == "malformed") _fileColumns["package"] = "unexpected-id";
        if (problem == "foreign-record") _returnedFileId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.DownloadFailed, error.ErrorCode);
        Assert.Empty(_requests);
        AssertLeasesDisposed();
    }

    [Fact]
    public async Task ChunkedDownload_UsesRemainingLengthsAndOffsets()
    {
        HasFile(size: ChunkSize + 3);
        var result = await _service.DownloadPackageAsync(_packageId);
        Assert.Equal(_fileBytes, result.Content);
        var blocks = _requests.Where(r => r.RequestName == "DownloadBlock").ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal(0L, blocks[0]["Offset"]);
        Assert.Equal((long)ChunkSize, blocks[0]["BlockLength"]);
        Assert.Equal((long)ChunkSize, blocks[1]["Offset"]);
        Assert.Equal(3L, blocks[1]["BlockLength"]);
        AssertLeasesDisposed();
    }

    [Fact]
    public async Task UnchunkedDownload_RequestsEntireFile()
    {
        HasFile(size: ChunkSize + 3);
        _initialized.Results["IsChunkingSupported"] = false;
        var result = await _service.DownloadPackageAsync(_packageId);
        Assert.Equal(_fileBytes, result.Content);
        var block = Assert.Single(_requests, r => r.RequestName == "DownloadBlock");
        Assert.Equal(0L, block["Offset"]);
        Assert.Equal((long)_fileBytes.Length, block["BlockLength"]);
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData("missing-token")]
    [InlineData("empty-token")]
    [InlineData("wrong-token-type")]
    [InlineData("missing-size")]
    [InlineData("negative-size")]
    [InlineData("zero-size")]
    [InlineData("excessive-size")]
    [InlineData("wrong-size-type")]
    [InlineData("missing-chunking")]
    [InlineData("wrong-chunking-type")]
    public async Task InvalidInitialization_StopsBeforeBlocks(string problem)
    {
        HasFile();
        switch (problem)
        {
            case "missing-token": _initialized.Results.Remove("FileContinuationToken"); break;
            case "empty-token": _initialized.Results["FileContinuationToken"] = " "; break;
            case "wrong-token-type": _initialized.Results["FileContinuationToken"] = 1; break;
            case "missing-size": _initialized.Results.Remove("FileSizeInBytes"); break;
            case "negative-size": _initialized.Results["FileSizeInBytes"] = -1L; break;
            case "zero-size": _initialized.Results["FileSizeInBytes"] = 0L; break;
            case "excessive-size": _initialized.Results["FileSizeInBytes"] = long.MaxValue; break;
            case "wrong-size-type": _initialized.Results["FileSizeInBytes"] = 3; break;
            case "missing-chunking": _initialized.Results.Remove("IsChunkingSupported"); break;
            case "wrong-chunking-type": _initialized.Results["IsChunkingSupported"] = "true"; break;
        }
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.DownloadFailed, error.ErrorCode);
        Assert.Single(_requests);
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task InvalidBlock_StopsWithoutReturningPartialFile(string problem)
    {
        HasFile(size: ChunkSize + 3);
        _executeOverride = request =>
        {
            if (request.RequestName == "InitializeFileBlocksDownload") return _initialized;
            return problem switch
            {
                "missing" => new OrganizationResponse(),
                "null" => Response(("Data", null!)),
                "empty" => Response(("Data", Array.Empty<byte>())),
                "short" => Response(("Data", new byte[ChunkSize - 1])),
                _ => Response(("Data", new byte[ChunkSize + 1]))
            };
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.DownloadFailed, error.ErrorCode);
        Assert.Equal(2, _requests.Count);
        AssertLeasesDisposed();
    }

    [Fact]
    public async Task CancellationAfterBlock_StopsNextRequest()
    {
        HasFile(size: ChunkSize + 3);
        using var cancellation = new CancellationTokenSource();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.ReportProgress(It.IsAny<ProgressSnapshot>())).Callback(() => cancellation.Cancel());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.DownloadPackageAsync(_packageId, cancellation.Token, progress.Object));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, _requests.Count);
        AssertLeasesDisposed();
    }

    [Fact]
    public async Task SdkFailure_IsCodedWithoutTokenLeak()
    {
        HasFile();
        var failure = new InvalidOperationException("private diagnostics with opaque-token");
        _executeOverride = request => request.RequestName == "DownloadBlock" ? throw failure : _initialized;
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.DownloadFailed, error.ErrorCode);
        Assert.Same(failure, error.InnerException);
        Assert.DoesNotContain("opaque-token", error.Message);
        Assert.DoesNotContain("private diagnostics", error.UserMessage);
        Assert.Equal(2, _requests.Count);
        AssertLeasesDisposed();
    }

    [Fact]
    public async Task InvalidLegacyBase64_IsCoded()
    {
        _legacyContent = "invalid base64!";
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ErrorCodes.Plugin.DownloadFailed, error.ErrorCode);
        Assert.IsType<FormatException>(error.InnerException);
        Assert.Empty(_requests);
        AssertLeasesDisposed();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("permissions")]
    [InlineData("credentials")]
    [InlineData("credential-code")]
    [InlineData("connection")]
    [InlineData("throttle")]
    [InlineData("timeout")]
    public async Task ConnectionFailure_PreservesRecoveryClassification(string kind)
    {
        Exception failure = kind switch
        {
            "expired" => new DataverseAuthenticationException("opaque-token", true),
            "permissions" => new DataverseAuthenticationException("opaque-token", false),
            "credentials" => new AuthenticationException("opaque-token"),
            "credential-code" => new AuthenticationException("opaque-token", ErrorCodes.Auth.Expired),
            "connection" => new DataverseConnectionException("opaque-token"),
            "throttle" => new ServiceProtectionException("opaque-token"),
            _ => new TimeoutException("opaque-token")
        };
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var error = await Assert.ThrowsAnyAsync<PpdsException>(() => _service.DownloadPackageAsync(_packageId));
        Assert.Equal(ExceptionMapper.Map(failure).Code, ExceptionMapper.Map(error).Code);
        Assert.Equal(ExceptionMapper.ToExitCode(failure), ExceptionMapper.ToExitCode(error));
        Assert.Same(failure, error.InnerException);
        Assert.DoesNotContain("opaque-token", error.UserMessage);
        if (failure is DataverseAuthenticationException auth)
            Assert.Equal(auth.RequiresReauthentication, Assert.IsType<PpdsAuthException>(error).RequiresReauthentication);
        Assert.Empty(_requests);
        Assert.Empty(_leases);
    }

    [Fact]
    public async Task ProgressOverload_ReportsVerifiedByteCountsAndIsCompatible()
    {
        HasFile(size: ChunkSize + 3);
        var snapshots = new List<ProgressSnapshot>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.ReportProgress(It.IsAny<ProgressSnapshot>())).Callback<ProgressSnapshot>(snapshots.Add);
        IPluginRegistrationService service = _service;
        var result = await service.DownloadPackageAsync(_packageId, default, progress.Object);
        Assert.Equal(_fileBytes, result.Content);
        Assert.Equal(new[] { ChunkSize, ChunkSize + 3 }, snapshots.Select(s => s.CurrentItem));
        Assert.All(snapshots, snapshot => Assert.Equal(_fileBytes.Length, snapshot.TotalItems));
        var overload = typeof(IPluginRegistrationService).GetMethod(nameof(IPluginRegistrationService.DownloadPackageAsync),
            [typeof(Guid), typeof(CancellationToken), typeof(IProgressReporter)]);
        Assert.NotNull(overload);
        Assert.False(overload.IsAbstract);
        AssertLeasesDisposed();
    }
}
