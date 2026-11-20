using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Auth.DependencyInjection;
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
    private readonly object _lock = new();
    private TaskCompletionSource<ProviderCreationRecord> _firstCreation = NewCreationSignal();

    /// <summary>
    /// Gets the log of all provider creation calls.
    /// </summary>
    public IReadOnlyList<ProviderCreationRecord> CreationLog
    {
        get { lock (_lock) return _creationLog.ToList(); }
    }

    /// <summary>
    /// Completes with the first creation recorded since construction or the last <see cref="Reset"/>,
    /// so a test can wait for a fire-and-forget creation instead of sleeping.
    /// </summary>
    public Task<ProviderCreationRecord> FirstCreationSinceReset
    {
        get { lock (_lock) return _firstCreation.Task; }
    }

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
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
        CancellationToken cancellationToken = default)
    {
        var record = new ProviderCreationRecord(profileName, environmentUrl, DateTime.UtcNow);
        TaskCompletionSource<ProviderCreationRecord> signal;
        lock (_lock)
        {
            _creationLog.Add(record);
            signal = _firstCreation;
        }
        signal.TrySetResult(record);

        if (CreateDelay > TimeSpan.Zero)
        {
            await Task.Delay(CreateDelay, cancellationToken);
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        var services = new ServiceCollection();

        // Register auth services
        services.AddAuthServices();

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
        lock (_lock)
        {
            _creationLog.Clear();
            _firstCreation = NewCreationSignal();
        }
        ExceptionToThrow = null;
        CreateDelay = TimeSpan.Zero;
    }

    private static TaskCompletionSource<ProviderCreationRecord> NewCreationSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Record of a provider creation call.
/// </summary>
public sealed record ProviderCreationRecord(
    string? ProfileName,
    string EnvironmentUrl,
    DateTime CreatedAt);
