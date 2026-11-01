using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PPDS.Auth.Credentials;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Shared context for all MCP tools providing access to connection pools and services.
/// </summary>
/// <remarks>
/// This class encapsulates the common operations needed by MCP tools:
/// - Loading and validating the active profile
/// - Getting or creating connection pools
/// - Creating service providers with full DI for service access
/// </remarks>
public sealed class McpToolContext : IDisposable
{
    private readonly IMcpConnectionPoolManager _poolManager;
    private readonly ProfileStore _profileStore;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly McpSessionOptions _sessionOptions;
    private AuthProfile? _lockedProfile;
    private string? _currentEnvironmentUrl;
    private EnvironmentInfo? _currentEnvironment;
    private string? _startupError;
    private bool _initialized;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly EnvironmentAcknowledgementStore _acknowledgements = new();
    private readonly FormMutationCoordinator _formMutations = new();
    // A protocol-test seam: production always creates the real provider from captured connection sources.
    internal Func<ResolvedConnectionInfo, IEnvironmentConfirmationContext, EnvironmentAcknowledgementStore, FormMutationCoordinator, ServiceProvider>? ServiceProviderFactory { get; init; }
    private readonly SemaphoreSlim _lockGate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolContext"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager.</param>
    /// <param name="profileStore">The profile store for loading/saving auth profiles.</param>
    /// <param name="credentialStore">The secure credential store.</param>
    /// <param name="sessionOptions">Session configuration options.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public McpToolContext(
        IMcpConnectionPoolManager poolManager,
        ProfileStore profileStore,
        ISecureCredentialStore credentialStore,
        McpSessionOptions sessionOptions,
        ILoggerFactory? loggerFactory = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        ArgumentNullException.ThrowIfNull(sessionOptions);
        _sessionOptions = new McpSessionOptions { Profile = sessionOptions.Profile, Environment = sessionOptions.Environment,
            ReadOnly = sessionOptions.ReadOnly, AllowedEnvironments = [.. sessionOptions.AllowedEnvironments] };
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>Whether DML and guarded form mutations are disabled for this session.</summary>
    public bool IsReadOnly => _sessionOptions.ReadOnly;

    /// <summary>The overridden environment URL, if specified via --environment.</summary>
    public string? EnvironmentUrlOverride => _currentEnvironmentUrl ?? _sessionOptions.Environment;

    /// <summary>
    /// Validates whether switching to the given environment URL is allowed.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the environment is not in the allowlist.</exception>
    public void ValidateEnvironmentSwitch(string environmentUrl)
    {
        if (!_sessionOptions.IsEnvironmentAllowed(environmentUrl))
        {
            var msg = _sessionOptions.AllowedEnvironments.Count == 0
                ? "Environment switching is disabled for this MCP session. The session is locked to the initial environment."
                : $"Environment '{environmentUrl}' is not in the allowed list. Allowed: {string.Join(", ", _sessionOptions.AllowedEnvironments)}";
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Gets the active authentication profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no profile is active.</exception>
    public async Task<AuthProfile> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _lockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockedProfile is null)
                throw new InvalidOperationException(_startupError ?? "No active profile configured at session startup.");
            return CaptureProfile();
        }
        finally { _lockGate.Release(); }
    }

    /// <summary>Captures defaults before the MCP host accepts requests. Missing auth remains a useful tool error.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _lockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var profile = _sessionOptions.Profile is not null
                ? collection.GetByNameOrIndex(_sessionOptions.Profile)
                : collection.ActiveProfile;
            if (profile is null)
                _startupError = "No active profile configured at session startup. Run 'ppds auth create', then restart this MCP server.";
            else
            {
                _lockedProfile = CloneProfile(profile);
                var url = _sessionOptions.Environment ?? profile.Environment?.Url;
                _currentEnvironmentUrl = url is null ? null : EnvironmentConfirmationService.NormalizeUrl(url);
                _currentEnvironment = _currentEnvironmentUrl is null ? null :
                    profile.Environment is not null && EnvironmentConfirmationService.NormalizeUrl(profile.Environment.Url) == _currentEnvironmentUrl
                        ? CloneEnvironment(profile.Environment)
                        : new EnvironmentInfo { Url = _currentEnvironmentUrl, DisplayName = _currentEnvironmentUrl };
                if (_currentEnvironment is not null) _currentEnvironment.Url = _currentEnvironmentUrl!;
            }
            _initialized = true;
        }
        finally { _lockGate.Release(); }
    }

    private static AuthProfile CloneProfile(AuthProfile profile)
        => JsonSerializer.Deserialize<AuthProfile>(JsonSerializer.Serialize(profile))!;

    private static EnvironmentInfo? CloneEnvironment(EnvironmentInfo? environment)
        => environment is null ? null : JsonSerializer.Deserialize<EnvironmentInfo>(JsonSerializer.Serialize(environment))!;

    // Called only while holding the target gate: identity and current environment stay one snapshot.
    private AuthProfile CaptureProfile()
    {
        var profile = CloneProfile(_lockedProfile!);
        profile.Environment = CloneEnvironment(_currentEnvironment);
        return profile;
    }

    /// <summary>Changes only this session's future operations; captured operations keep their own target.</summary>
    public async Task SwitchEnvironmentAsync(EnvironmentInfo environment, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var target = EnvironmentConfirmationService.NormalizeUrl(environment.Url);
        await _lockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockedProfile is null) throw new InvalidOperationException(_startupError);
            if (!string.Equals(target, _currentEnvironmentUrl, StringComparison.Ordinal))
                ValidateEnvironmentSwitch(target);
            _currentEnvironmentUrl = target;
            _currentEnvironment = CloneEnvironment(environment);
            _currentEnvironment!.Url = target;
        }
        finally { _lockGate.Release(); }
        // Retain old pools until server shutdown: in-flight calls still own their target.
    }

    private async Task<(AuthProfile Profile, string Url)> CaptureTargetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _lockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockedProfile is null) throw new InvalidOperationException(_startupError);
            var url = _currentEnvironmentUrl ?? throw new InvalidOperationException(
                "No environment selected at session startup. Select an allowed environment or restart with --environment.");
            return (CaptureProfile(), url);
        }
        finally { _lockGate.Release(); }
    }

    public void Dispose()
    {
        _lockGate.Dispose();
        _formMutations.Dispose();
    }

    /// <summary>
    /// Gets the profile collection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile collection.</returns>
    public async Task<ProfileCollection> GetProfileCollectionAsync(CancellationToken cancellationToken = default)
    {
        var store = _profileStore;
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or creates a connection pool for the active profile's environment.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connection pool for the active profile's environment.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no profile is active or no environment is selected.
    /// </exception>
    public async Task<IDataverseConnectionPool> GetPoolAsync(CancellationToken cancellationToken = default)
    {
        var (profile, environmentUrl) = await CaptureTargetAsync(cancellationToken).ConfigureAwait(false);
        return await _poolManager.GetOrCreateCapturedPoolAsync(profile, environmentUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a service provider with full DI for the active profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A service provider configured for the active profile's environment.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no profile is active or no environment is selected.
    /// </exception>
    /// <remarks>
    /// The returned service provider should be disposed after use.
    /// For most operations, prefer <see cref="GetPoolAsync"/> which uses cached pools.
    /// Use this method when you need access to services like IMetadataQueryService or ISqlQueryService.
    /// </remarks>
    public async Task<ServiceProvider> CreateServiceProviderAsync(CancellationToken cancellationToken = default)
    {
        var (profile, environmentUrl) = await CaptureTargetAsync(cancellationToken).ConfigureAwait(false);
        var environmentDisplayName = profile.Environment?.Url.TrimEnd('/') == environmentUrl
            ? profile.Environment.DisplayName : environmentUrl;

        if (ServiceProviderFactory is not null)
            return ServiceProviderFactory(
                new ResolvedConnectionInfo { Profile = profile, EnvironmentUrl = environmentUrl, EnvironmentDisplayName = environmentDisplayName },
                EnvironmentConfirmationContext.FromProfile(profile, environmentUrl, _sessionId, IsReadOnly),
                _acknowledgements, _formMutations);

        var sources = new List<IConnectionSource>();

        try
        {
            var source = new ProfileConnectionSource(
                profile,
                environmentUrl,
                maxPoolSize: 52,
                deviceCodeCallback: null,
                environmentDisplayName: environmentDisplayName,
                credentialStore: _credentialStore);

            var adapter = new ProfileConnectionSourceAdapter(source);
            sources.Add(adapter);

            return CreateProviderFromSources(sources.ToArray(), _credentialStore, profile, environmentUrl, environmentDisplayName);
        }
        catch
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Saves the profile collection after modifications (e.g., environment selection).
    /// </summary>
    /// <param name="collection">The modified profile collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveProfileCollectionAsync(ProfileCollection collection, CancellationToken cancellationToken = default)
    {
        var store = _profileStore;
        await store.SaveAsync(collection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the cached pool for the current environment after environment change.
    /// </summary>
    /// <param name="environmentUrl">The environment URL that was changed.</param>
    public void InvalidateEnvironment(string environmentUrl)
    {
        _poolManager.InvalidateEnvironment(environmentUrl);
    }

    /// <summary>
    /// Creates a service provider from connection sources.
    /// </summary>
    private ServiceProvider CreateProviderFromSources(
        IConnectionSource[] sources,
        ISecureCredentialStore credentialStore,
        AuthProfile profile,
        string environmentUrl,
        string? environmentDisplayName)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ResolvedConnectionInfo { Profile = profile, EnvironmentUrl = environmentUrl,
            EnvironmentDisplayName = environmentDisplayName });
        services.AddSingleton<IEnvironmentConfirmationContext>(EnvironmentConfirmationContext.FromProfile(
            profile, environmentUrl, _sessionId, IsReadOnly));
        services.AddSingleton(_acknowledgements);
        services.AddSingleton(_formMutations);

        // Configure minimal logging.
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(new LoggerFactoryProvider(_loggerFactory));
        });

        // Register credential store FIRST (DI owns lifecycle, not this child provider).
        // AddCliApplicationServices → AddAuthServices uses TryAddSingleton, so this
        // per-tool-context instance wins over the default NativeCredentialStore.
        services.AddSingleton<ISecureCredentialStore>(credentialStore);

        var dataverseOptions = new DataverseOptions();
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true
        };

        // Register shared services (IThrottleTracker, IBulkOperationExecutor, IMetadataQueryService, etc.).
        services.RegisterDataverseServices();

        // Register CLI application services — the 12 domain services (IPluginTraceService,
        // ISolutionService, IWebResourceService, …) relocated out of PPDS.Dataverse now live
        // here, plus IShakedownGuard. MCP tools resolve these from this child provider, so
        // they must be registered here too (not only in PPDS.Mcp.Program.cs).
        services.AddCliApplicationServices();

        // Connection pool with factory delegate.
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                sources,
                sp.GetRequiredService<IThrottleTracker>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Simple logger provider that wraps an existing ILoggerFactory.
    /// </summary>
    private sealed class LoggerFactoryProvider : ILoggerProvider
    {
        private readonly ILoggerFactory _factory;

        public LoggerFactoryProvider(ILoggerFactory factory)
        {
            _factory = factory;
        }

        public ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

        public void Dispose()
        {
            // Don't dispose the factory - it's owned externally.
        }
    }
}
