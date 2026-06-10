using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Authoring = PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Security;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Cli.Services.ConnectionReferences.RelationshipType;
using WebResourceInfoModel = PPDS.Cli.Services.WebResources.WebResourceInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

/// <summary>
/// Handles JSON-RPC method calls for the serve daemon.
/// Method naming follows the CLI command structure: "group/subcommand".
/// </summary>
public partial class RpcMethodHandler : IDisposable
{
    private readonly IDaemonConnectionPoolManager _poolManager;
    private readonly IServiceProvider _authServices;
    private readonly ILogger<RpcMethodHandler> _logger;
    private readonly CancellationTokenSource _daemonCts = new();
    private readonly DateTimeOffset _daemonStartedAt = DateTimeOffset.UtcNow;
    private long _heartbeatCount;
    private JsonRpc? _rpc;

    // Discovery cache for env/list — keyed by profile name so each profile's discovered
    // environments are cached independently. ConcurrentDictionary provides thread-safe
    // reads and writes without explicit locking.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<EnvironmentInfo> environments, long expiry)> _envCacheByProfile = new();
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMethodHandler"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager for caching Dataverse pools.</param>
    /// <param name="authServices">Service provider for auth services (ProfileStore, ISecureCredentialStore).</param>
    /// <param name="logger">Optional logger. If null, a NullLogger is used.</param>
    public RpcMethodHandler(
        IDaemonConnectionPoolManager poolManager,
        IServiceProvider authServices,
        ILogger<RpcMethodHandler>? logger = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _authServices = authServices ?? throw new ArgumentNullException(nameof(authServices));
        _logger = logger ?? NullLogger<RpcMethodHandler>.Instance;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _daemonCts.Cancel();
        _daemonCts.Dispose();
    }

    /// <summary>
    /// Sets the JSON-RPC context for sending notifications (e.g., device code flow).
    /// Must be called exactly once after JsonRpc.Attach.
    /// </summary>
    /// <param name="rpc">The JSON-RPC connection.</param>
    /// <exception cref="InvalidOperationException">Thrown if called more than once.</exception>
    public void SetRpcContext(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        if (Interlocked.CompareExchange(ref _rpc, rpc, null) != null)
        {
            throw new InvalidOperationException("RPC context has already been set.");
        }
    }

    #region Daemon Health

    /// <summary>
    /// Lightweight liveness probe used by the VS Code extension (and any other client)
    /// to detect a dead daemon. Clients are expected to call this every ~30s; a missed
    /// response means the daemon has hung or exited and the client should restart it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Method name:</b> <c>_heartbeat</c> — the underscore prefix flags this as an
    /// internal/infrastructure RPC, not a surface-level command, so it is greppable
    /// in logs as <c>method=_heartbeat</c>.
    /// </para>
    /// <para>
    /// <b>How to call from the extension:</b>
    /// <code>
    /// const result = await connection.sendRequest&lt;HeartbeatResponse&gt;('_heartbeat');
    /// // result.ok === true, result.uptimeSeconds, result.correlationId
    /// </code>
    /// Follow-up: the Extension should wire this into <c>DaemonStatusBar</c> as a
    /// 30s timer; missing two consecutive responses → show "daemon unreachable".
    /// </para>
    /// <para>
    /// The response correlation id is deliberately unique per call so support can
    /// join a client-side "lost heartbeat" incident with the daemon stderr log.
    /// </para>
    /// </remarks>
    [JsonRpcMethod("_heartbeat")]
    public Task<HeartbeatResponse> HeartbeatAsync()
    {
        return SafeExecuteAsync("_heartbeat", () =>
        {
            var count = Interlocked.Increment(ref _heartbeatCount);
            var uptime = DateTimeOffset.UtcNow - _daemonStartedAt;

            // Emit at Debug so ppds logs filtered at Debug can trace heartbeats, but
            // Information-level defaults stay uncluttered during healthy operation.
            _logger.LogDebug(
                "_heartbeat ok count={Count} uptimeSec={Uptime:F0}",
                count,
                uptime.TotalSeconds);

            return Task.FromResult(new HeartbeatResponse
            {
                Ok = true,
                UptimeSeconds = (long)uptime.TotalSeconds,
                StartedAt = _daemonStartedAt,
                CorrelationId = CorrelationIdScope.Current ?? string.Empty,
                HeartbeatCount = count,
                DaemonVersion = typeof(RpcMethodHandler).Assembly.GetName().Version?.ToString() ?? "unknown"
            });
        });
    }

    /// <summary>
    /// Response payload for the <c>_heartbeat</c> method. Clients should treat
    /// <see cref="Ok"/>=true as "daemon responsive". Uptime and start time are
    /// useful for debugging long-running sessions.
    /// </summary>
    public sealed class HeartbeatResponse
    {
        /// <summary>Always <see langword="true"/> when the daemon responds.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>Daemon uptime in seconds since the serve command started.</summary>
        [JsonPropertyName("uptimeSeconds")]
        public long UptimeSeconds { get; set; }

        /// <summary>UTC timestamp when the daemon started.</summary>
        [JsonPropertyName("startedAt")]
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>Correlation id associated with this heartbeat call.</summary>
        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; } = string.Empty;

        /// <summary>Total heartbeats served since daemon startup. Useful for detecting client reconnects.</summary>
        [JsonPropertyName("heartbeatCount")]
        public long HeartbeatCount { get; set; }

        /// <summary>Daemon (PPDS.Cli) assembly version.</summary>
        [JsonPropertyName("daemonVersion")]
        public string DaemonVersion { get; set; } = string.Empty;
    }

    #endregion

    #region Shared Helpers

    /// <summary>
    /// Executes an action with the validated active profile, its environment, and a cached service provider.
    /// Profile and environment are loaded once, validated, then passed directly into the lambda —
    /// eliminating the need for callers to reload the store or use null-forgiving operators.
    /// The service provider is long-lived (cached by the pool manager) — do NOT dispose it inside the action.
    /// </summary>
    /// <typeparam name="T">The return type of the action.</typeparam>
    /// <param name="action">The action to execute with the service provider, profile, and environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the action.</returns>
    /// <exception cref="RpcException">Thrown when no active profile or environment is configured.</exception>
    private Task<T> WithActiveProfileAsync<T>(
        Func<IServiceProvider, AuthProfile, PPDS.Auth.Profiles.EnvironmentInfo, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        return SafeExecuteAsync(callerName, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var store = _authServices.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile
                ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

            var environment = profile.Environment
                ?? throw new RpcException(
                    ErrorCodes.Connection.EnvironmentNotFound,
                    "No environment selected. Use env/select first.");

            // Use the pool manager to get a cached service provider. This reuses the existing
            // connection pool instead of creating a new ServiceClient on every RPC call.
            var activeProfileName = profile.Name ?? profile.DisplayIdentifier;
            var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(
                new[] { activeProfileName },
                environment.Url,
                deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc, activeProfileName),
                cancellationToken: cancellationToken);

            return await action(serviceProvider, profile, environment, cancellationToken);
        });
    }

    /// <summary>
    /// Convenience overload for actions that only need the service provider and cancellation token.
    /// Wraps the full overload, discarding the profile and environment parameters.
    /// </summary>
    /// <remarks>
    /// <c>callerName</c> is forwarded so <see cref="SafeExecuteAsync{T}"/>'s correlation log line
    /// identifies the real RPC method, not this shim.
    /// </remarks>
    private Task<T> WithActiveProfileAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
        => WithActiveProfileAsync<T>(
            (sp, _, _, ct) => action(sp, ct),
            cancellationToken,
            callerName);

    /// <summary>
    /// Resolves the display name for an environment URL, preferring the user's configured label
    /// from environments.json over the raw discovery/profile display name.
    /// </summary>
    private async Task<string> ResolveEnvironmentLabelAsync(string url, string fallbackDisplayName, CancellationToken ct)
    {
        try
        {
            var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
            var config = await configStore.GetConfigAsync(url, ct);
            if (config?.Label != null) return config.Label;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Config label lookup failed for {Url}", url); }
        return fallbackDisplayName;
    }

    /// <summary>
    /// Executes an action with the specified (or active) profile's credentials against a specific environment.
    /// If profileName is provided, resolves that profile by name or index; otherwise uses the active profile.
    /// If environmentUrl is provided, uses it; otherwise falls back to the resolved profile's saved environment.
    /// </summary>
    private Task<T> WithProfileAndEnvironmentAsync<T>(
        string? profileName,
        string? environmentUrl,
        Func<IServiceProvider, AuthProfile, PPDS.Auth.Profiles.EnvironmentInfo, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        return SafeExecuteAsync(callerName, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var store = _authServices.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(cancellationToken);

            AuthProfile profile;
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                profile = collection.GetByNameOrIndex(profileName)
                    ?? throw new RpcException(ErrorCodes.Auth.ProfileNotFound, $"Profile '{profileName}' not found");
            }
            else
            {
                profile = collection.ActiveProfile
                    ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
            }

            // Resolve environment: explicit URL wins, else profile's saved environment
            string resolvedUrl;
            if (!string.IsNullOrWhiteSpace(environmentUrl))
            {
                resolvedUrl = environmentUrl;
            }
            else
            {
                var env = profile.Environment
                    ?? throw new RpcException(
                        ErrorCodes.Connection.EnvironmentNotFound,
                        "No environment selected. Use env/select first.");
                resolvedUrl = env.Url;
            }

            // Build an EnvironmentInfo for the resolved URL
            var resolvedEnvironment = profile.Environment?.Url?.Equals(resolvedUrl, StringComparison.OrdinalIgnoreCase) == true
                ? profile.Environment
                : new PPDS.Auth.Profiles.EnvironmentInfo { Url = resolvedUrl, DisplayName = resolvedUrl };

            var resolvedProfileName = profile.Name ?? profile.DisplayIdentifier;
            var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(
                new[] { resolvedProfileName },
                resolvedUrl,
                deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc, resolvedProfileName),
                cancellationToken: cancellationToken);

            return await action(serviceProvider, profile, resolvedEnvironment, cancellationToken);
        });
    }

    /// <summary>
    /// Convenience overload for actions that only need the service provider and cancellation token.
    /// </summary>
    /// <remarks>
    /// <c>callerName</c> is forwarded to the full overload so <see cref="SafeExecuteAsync{T}"/>'s
    /// correlation log line identifies the real RPC method, not this shim.
    /// </remarks>
    private Task<T> WithProfileAndEnvironmentAsync<T>(
        string? profileName,
        string? environmentUrl,
        Func<IServiceProvider, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
        => WithProfileAndEnvironmentAsync<T>(
            profileName,
            environmentUrl,
            (sp, _, _, ct) => action(sp, ct),
            cancellationToken,
            callerName);

    /// <summary>
    /// Maps a PpdsException (or subclass) to an RpcException with structured error data.
    /// Preserves subclass-specific properties (RequiresReauthentication, RetryAfter, etc.).
    /// </summary>
    private static RpcException MapPpdsToRpcException(PpdsException ex)
    {
        var data = new RpcErrorData { Code = ex.ErrorCode, Message = ex.UserMessage };

        switch (ex)
        {
            case PpdsAuthException authEx:
                data.RequiresReauthentication = authEx.RequiresReauthentication;
                break;
            case PpdsThrottleException throttleEx:
                data.RetryAfterSeconds = throttleEx.RetryAfter.TotalSeconds;
                break;
            case PpdsValidationException validationEx:
                data.ValidationErrors = validationEx.Errors
                    .Select(e => new RpcValidationError { Field = e.Field, Message = e.Message })
                    .ToList();
                break;
            case PpdsNotFoundException notFoundEx:
                data.ResourceType = notFoundEx.ResourceType;
                data.ResourceId = notFoundEx.ResourceId;
                break;
        }

        return new RpcException(ex.ErrorCode, ex.UserMessage, data);
    }

    // The workspace root used to constrain RPC-exposed filesystem paths. Captured once at daemon
    // startup because the extension launches ppds serve with the workspace folder as the working
    // directory. Using a snapshot avoids TOCTOU issues if code elsewhere mutates CurrentDirectory.
    private static readonly string WorkspaceRoot = System.IO.Path.GetFullPath(Environment.CurrentDirectory);

    /// <summary>
    /// Defensive wrapper that converts unhandled exceptions into structured <see cref="RpcException"/>s.
    /// Without this wrapper, raw exceptions from a handler propagate to StreamJsonRpc which returns a
    /// generic JSON-RPC error without the hierarchical <c>ErrorCode</c> contract the extension relies on.
    /// Logs at Error with a correlation id so support scenarios can cross-reference daemon stderr.
    /// Never crashes the daemon — always translates to a response the client can render.
    /// </summary>
    /// <remarks>
    /// <see cref="RpcException"/>s (expected validation/auth/not-found failures) pass through unchanged.
    /// Only "surprise" exceptions (NRE, InvalidCast, IO, etc.) get the <see cref="ErrorCodes.Operation.Internal"/>
    /// treatment.
    /// </remarks>
    internal async Task<T> SafeExecuteAsync<T>(string methodName, Func<Task<T>> action)
    {
        // Install a fresh correlation id at the RPC boundary so each call gets its own
        // identifier in logs. Program.cs:Main pushes a process-lifetime bootstrap scope
        // which would otherwise leak into every RPC in daemon mode if we honored Current
        // here — the bug we previously had where every ppds serve request shared one id.
        // Nested calls (e.g., WithActiveProfileAsync → action) push their own scopes
        // beneath this one and inherit the fresh id naturally.
        var correlationId = CorrelationIdScope.NewId();
        using var scope = CorrelationIdScope.Push(correlationId);

        var startTicks = Environment.TickCount64;
        // Emit a greppable start marker so ppds logs / daemon stderr can pair start/end events.
        _logger.LogDebug(
            "rpc.start method={Method} correlationId={CorrelationId}",
            methodName,
            correlationId);

        try
        {
            var result = await action();
            _logger.LogDebug(
                "rpc.end method={Method} correlationId={CorrelationId} durationMs={DurationMs} outcome=success",
                methodName,
                correlationId,
                Environment.TickCount64 - startTicks);
            return result;
        }
        catch (RpcException rpcEx)
        {
            // Already structured; pass through. Log at Debug so ppds logs shows the failure origin.
            _logger.LogDebug(
                "rpc.end method={Method} correlationId={CorrelationId} durationMs={DurationMs} outcome=rpc-error code={Code}",
                methodName,
                correlationId,
                Environment.TickCount64 - startTicks,
                rpcEx.StructuredErrorCode);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is cooperative — surface as-is so StreamJsonRpc maps to the standard
            // cancellation response. Not a bug and not a daemon fault.
            _logger.LogDebug(
                "rpc.end method={Method} correlationId={CorrelationId} durationMs={DurationMs} outcome=cancelled",
                methodName,
                correlationId,
                Environment.TickCount64 - startTicks);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "rpc.error method={Method} correlationId={CorrelationId}",
                methodName,
                correlationId);

            // Redact the exception message to avoid leaking filesystem paths, connection strings,
            // or other operator-only details to the remote client. The correlationId is the join
            // key between the thin client message and the detailed server log.
            throw new RpcException(
                ErrorCodes.Operation.Internal,
                $"Internal error in {methodName}. CorrelationId: {correlationId}.");
        }
    }

    /// <summary>
    /// Resolves a user-supplied path and requires the canonical result to live under the workspace root.
    /// RPC is a hostile boundary: without this check, a malicious webview message could target arbitrary
    /// filesystem locations (e.g., "..\\..\\Users\\x\\.ssh\\id_rsa").
    /// </summary>
    /// <param name="userInput">The path supplied by the RPC caller. May be relative or absolute.</param>
    /// <param name="parameterName">Name of the RPC parameter (for the error message).</param>
    /// <returns>The canonical absolute path, guaranteed to be within <see cref="WorkspaceRoot"/>.</returns>
    /// <exception cref="RpcException">If the resolved path escapes the workspace root.</exception>
    internal static string ResolveWorkspacePath(string userInput, string parameterName)
    {
        return ResolveWorkspacePath(userInput, parameterName, WorkspaceRoot);
    }

    /// <summary>
    /// Test-friendly overload that accepts an explicit root. Production code routes through the
    /// zero-arg overload which uses <see cref="WorkspaceRoot"/> captured at startup.
    /// </summary>
    internal static string ResolveWorkspacePath(string userInput, string parameterName, string root)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                $"The '{parameterName}' parameter is required");
        }

        // Path.GetFullPath normalizes "..", symlinks-in-name, and relative roots to an absolute path.
        // Relative paths resolve against the provided root (the workspace folder at daemon startup).
        var resolved = System.IO.Path.GetFullPath(userInput, root);

        // Use a trailing separator to prevent prefix-match false positives like
        // "C:\\Workspace" matching "C:\\Workspace-evil\\secret".
        var rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.Equals(root, comparison) &&
            !resolved.StartsWith(rootWithSep, comparison))
        {
            throw new RpcException(
                ErrorCodes.Validation.PathOutsideWorkspace,
                $"The '{parameterName}' parameter resolves to a path outside the workspace root.");
        }

        return resolved;
    }

    #endregion
}
