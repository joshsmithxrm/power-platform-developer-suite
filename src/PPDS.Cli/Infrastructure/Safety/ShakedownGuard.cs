using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Default <see cref="IShakedownGuard"/> implementation. Thread-safe
/// singleton. Activation signals are re-resolved at most once per
/// <see cref="CacheTtl"/> under a single monitor so concurrent callers see
/// a consistent view.
/// </summary>
public sealed class ShakedownGuard : IShakedownGuard
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(24);

    private const string EnvVarName = "PPDS_SHAKEDOWN";
    private const string ProjectDirEnvVar = "CLAUDE_PROJECT_DIR";
    private const string SentinelRelPath = ".claude/state/shakedown-active.json";

    // Case-sensitive allowlist of "truthy but not '1'" values that should warn
    // the operator about the likely typo / misconfiguration. Any other
    // non-"1" value (empty, "0", "2", "garbage", ...) is silently inactive.
    private static readonly HashSet<string> TruthyNonOneWarnValues = new(StringComparer.Ordinal)
    {
        "true", "True", "TRUE",
        "yes", "Yes", "YES",
        "on", "ON",
    };

    private readonly IEnvironment _env;
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly ILogger<ShakedownGuard> _log;

    // Activation state cache, re-resolved on TTL expiry. resolvedAt defaults
    // to MinValue so the very first call is always a miss.
    private (DateTimeOffset resolvedAt, ActivationState state) _cache
        = (DateTimeOffset.MinValue, default);
    private readonly object _gate = new();

    /// <summary>
    /// Creates a new <see cref="ShakedownGuard"/>.
    /// </summary>
    public ShakedownGuard(
        IEnvironment env,
        IFileSystem fs,
        IClock clock,
        ILogger<ShakedownGuard> log)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public void EnsureCanMutate(string operationDescription)
    {
        if (string.IsNullOrWhiteSpace(operationDescription))
        {
            throw new ArgumentException(
                "operationDescription is required.",
                nameof(operationDescription));
        }

        var state = GetState();
        if (!state.IsActive) return;

        var ctx = new Dictionary<string, object>
        {
            ["operation"] = operationDescription,
            ["activationSource"] = state.Source,
        };
        if (state.SentinelRelativePath is not null)
            ctx["sentinelPath"] = state.SentinelRelativePath;
        if (state.SentinelAge.HasValue)
            ctx["sentinelAgeSeconds"] = state.SentinelAge.Value.TotalSeconds;

        throw new PpdsException(
            ErrorCodes.Safety.ShakedownActive,
            BuildUserMessage(operationDescription, state),
            ctx);
    }

    private ActivationState GetState()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (now - _cache.resolvedAt <= CacheTtl && _cache.resolvedAt != DateTimeOffset.MinValue)
                return _cache.state;

            var resolved = Resolve(now);
            _cache = (now, resolved);
            return resolved;
        }
    }

    private ActivationState Resolve(DateTimeOffset now)
    {
        // 1. Env var takes precedence.
        var envValue = _env.GetEnvironmentVariable(EnvVarName);
        if (envValue == "1")
        {
            return new ActivationState(
                IsActive: true,
                Source: "env:PPDS_SHAKEDOWN",
                SentinelRelativePath: null,
                SentinelAge: null);
        }

        // 2. Truthy-but-not-"1" values → operator likely meant "1"; warn.
        if (!string.IsNullOrEmpty(envValue) && TruthyNonOneWarnValues.Contains(envValue))
        {
            _log.LogWarning(
                "PPDS_SHAKEDOWN=\"{Value}\" was set but only \"1\" activates the shakedown guard; did you mean to set it to 1?",
                envValue);
        }

        // 3. Sentinel fallback. Resolve project root, then stat/parse.
        var projectRoot = ResolveProjectRoot();
        var absolutePath = Path.Combine(projectRoot, SentinelRelPath.Replace('/', Path.DirectorySeparatorChar));
        return ShakedownSentinelReader.Read(
            _fs,
            _log,
            absolutePath,
            SentinelRelPath,
            now,
            StaleThreshold);
    }

    private string ResolveProjectRoot()
    {
        var claudeProjectDir = _env.GetEnvironmentVariable(ProjectDirEnvVar);
        if (!string.IsNullOrEmpty(claudeProjectDir))
        {
            if (_fs.DirectoryExists(claudeProjectDir))
                return claudeProjectDir;

            _log.LogWarning(
                "CLAUDE_PROJECT_DIR=\"{StalePath}\" points at a directory that does not exist; falling back to current working directory for shakedown sentinel resolution.",
                claudeProjectDir);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string BuildUserMessage(string operationDescription, ActivationState state)
    {
        return $"Mutation '{operationDescription}' refused: shakedown session is active ({state.Source}). " +
               "Bypass: unset PPDS_SHAKEDOWN and/or remove .claude/state/shakedown-active.json.";
    }
}
