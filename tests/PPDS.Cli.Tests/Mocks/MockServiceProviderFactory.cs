using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tests.Mocks;

/// <summary>
/// Mock implementation of <see cref="IServiceProviderFactory"/> for testing.
/// Allows injecting fake services into InteractiveSession tests.
/// </summary>
public sealed class MockServiceProviderFactory : IServiceProviderFactory
{
    private readonly List<ProviderCreationRecord> _creationLog = new();
    private readonly Func<ServiceCollection, ServiceCollection>? _configureServices;

    /// <summary>
    /// Gets the log of all provider creation calls.
    /// </summary>
    public IReadOnlyList<ProviderCreationRecord> CreationLog => _creationLog;

    /// <summary>
    /// Gets or sets whether CreateAsync should throw an exception.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Gets or sets the delay before returning from CreateAsync.
    /// </summary>
    public TimeSpan CreateDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Creates a new mock factory with default fake services.
    /// </summary>
    public MockServiceProviderFactory()
    {
    }

    /// <summary>
    /// Creates a new mock factory with custom service configuration.
    /// </summary>
    /// <param name="configureServices">Function to configure additional services.</param>
    public MockServiceProviderFactory(Func<ServiceCollection, ServiceCollection> configureServices)
    {
        _configureServices = configureServices;
    }

    /// <inheritdoc />
    public async Task<ServiceProvider> CreateAsync(
        string? profileName,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        _creationLog.Add(new ProviderCreationRecord(
            profileName,
            environmentUrl,
            DateTime.UtcNow));

        if (CreateDelay > TimeSpan.Zero)
        {
            await Task.Delay(CreateDelay, cancellationToken);
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        var services = new ServiceCollection();

        // Register fake services
        services.AddSingleton<ISqlQueryService, FakeSqlQueryService>();
        services.AddSingleton<IQueryHistoryService, FakeQueryHistoryService>();
        services.AddSingleton<IExportService, FakeExportService>();

        // Allow custom configuration
        _configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Clears the creation log.
    /// </summary>
    public void Reset()
    {
        _creationLog.Clear();
        ExceptionToThrow = null;
        CreateDelay = TimeSpan.Zero;
    }
}

/// <summary>
/// Record of a provider creation call.
/// </summary>
public sealed record ProviderCreationRecord(
    string? ProfileName,
    string EnvironmentUrl,
    DateTime CreatedAt);
