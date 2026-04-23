using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Data;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// Unit tests for <see cref="DataQueryService"/> — verifies constructor guards,
/// DI registration, and argument validation (no Dataverse connection required).
/// </summary>
[Trait("Category", "Unit")]
public class DataQueryServiceTests
{
    #region Constructor / Guard Tests

    [Fact]
    public void Constructor_NullPool_ThrowsArgumentNullException()
    {
        var logger = new NullLogger<DataQueryService>();

        var act = () => new DataQueryService(null!, logger);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var pool = new Mock<IDataverseConnectionPool>().Object;

        var act = () => new DataQueryService(pool, null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<DataQueryService>();

        var service = new DataQueryService(pool, logger);

        service.Should().NotBeNull();
    }

    #endregion

    #region DI Registration Test

    [Fact]
    public void AddCliApplicationServices_RegistersIDataQueryService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IShakedownGuard, InactiveFakeShakedownGuard>();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();

        var provider = services.BuildServiceProvider();
        var dataQueryService = provider.GetService<IDataQueryService>();

        dataQueryService.Should().NotBeNull();
        dataQueryService.Should().BeOfType<DataQueryService>();
    }

    #endregion

    #region Input Validation Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ResolveByAlternateKeyAsync_BlankEntity_ThrowsArgumentNullException(string? entity)
    {
        var service = CreateService();

        var act = () => service.ResolveByAlternateKeyAsync(entity!, "field=value");

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("entity");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ResolveByAlternateKeyAsync_BlankKeyString_ThrowsArgumentNullException(string? keyString)
    {
        var service = CreateService();

        var act = () => service.ResolveByAlternateKeyAsync("account", keyString!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("keyString");
    }

    [Fact]
    public async Task ResolveByAlternateKeyAsync_InvalidKeyFormat_ThrowsPpdsValidationException()
    {
        var service = CreateService();

        // key without "=" is invalid
        var act = () => service.ResolveByAlternateKeyAsync("account", "notavalidpair");

        await act.Should().ThrowAsync<PpdsValidationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task QueryIdsByFilterAsync_BlankEntity_ThrowsArgumentNullException(string? entity)
    {
        var service = CreateService();

        var act = () => service.QueryIdsByFilterAsync(entity!, "name eq 'test'", null);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("entity");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task QueryIdsByFilterAsync_BlankFilter_ThrowsArgumentNullException(string? filter)
    {
        var service = CreateService();

        var act = () => service.QueryIdsByFilterAsync("account", filter!, null);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("whereFilter");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task FetchBatchIdsAsync_BlankEntity_ThrowsArgumentNullException(string? entity)
    {
        var service = CreateService();

        var act = () => service.FetchBatchIdsAsync(entity!, 100);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("entity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FetchBatchIdsAsync_InvalidBatchSize_ThrowsArgumentOutOfRangeException(int batchSize)
    {
        var service = CreateService();

        var act = () => service.FetchBatchIdsAsync("account", batchSize);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("batchSize");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CountRecordsAsync_BlankEntity_ThrowsArgumentNullException(string? entity)
    {
        var service = CreateService();

        var act = () => service.CountRecordsAsync(entity!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("entity");
    }

    #endregion

    #region Exception Wrapping Tests

    [Fact]
    public async Task ResolveByAlternateKeyAsync_DataverseThrows_WrapsPpdsException()
    {
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, default))
            .ThrowsAsync(new InvalidOperationException("Dataverse unavailable"));

        var service = new DataQueryService(pool.Object, new NullLogger<DataQueryService>());

        var act = () => service.ResolveByAlternateKeyAsync("account", "accountnumber=TEST");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    [Fact]
    public async Task CountRecordsAsync_DataverseThrows_WrapsPpdsException()
    {
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, default))
            .ThrowsAsync(new InvalidOperationException("Dataverse unavailable"));

        var service = new DataQueryService(pool.Object, new NullLogger<DataQueryService>());

        var act = () => service.CountRecordsAsync("account");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    [Fact]
    public async Task FetchBatchIdsAsync_DataverseThrows_WrapsPpdsException()
    {
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, default))
            .ThrowsAsync(new InvalidOperationException("Dataverse unavailable"));

        var service = new DataQueryService(pool.Object, new NullLogger<DataQueryService>());

        var act = () => service.FetchBatchIdsAsync("account", 100);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    #endregion

    #region QueryIdsByFilterAsync — Parse Error Tests

    [Fact]
    public async Task QueryIdsByFilterAsync_UnparsableFilter_ThrowsPpdsExceptionWithParseErrorCode()
    {
        var service = CreateService();

        // Deliberately un-parseable SQL
        var act = () => service.QueryIdsByFilterAsync("account", "!!@@##$$", null);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.ParseError);
    }

    #endregion

    // -------------------------------------------------------------------------
    private static DataQueryService CreateService()
    {
        // Use a pool mock that will never be called (validation tests don't reach the pool)
        var pool = new Mock<IDataverseConnectionPool>().Object;
        return new DataQueryService(pool, new NullLogger<DataQueryService>());
    }
}
