using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Environment;

/// <summary>Session-owned acknowledgements. Never persisted to the profile store.</summary>
public sealed class EnvironmentAcknowledgementStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _acknowledgements = new(StringComparer.Ordinal);

    internal bool Check(string key, string policy, bool acknowledge)
    {
        lock (_gate)
        {
            if (acknowledge)
            {
                _acknowledgements[key] = policy;
                return true;
            }

            if (_acknowledgements.TryGetValue(key, out var priorPolicy) && priorPolicy == policy)
                return true;

            _acknowledgements.Remove(key);
            return false;
        }
    }
}

/// <summary>Enforces the effective write protection for a captured operation target.</summary>
public sealed class EnvironmentConfirmationService : IEnvironmentConfirmationService
{
    private readonly IEnvironmentConfigService _configuration;
    private readonly EnvironmentAcknowledgementStore _acknowledgements;

    public IEnvironmentConfirmationContext Context { get; }

    public EnvironmentConfirmationService(
        IEnvironmentConfirmationContext context,
        IEnvironmentConfigService configuration,
        EnvironmentAcknowledgementStore acknowledgements)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _acknowledgements = acknowledgements ?? throw new ArgumentNullException(nameof(acknowledgements));
    }

    public async Task EnsureConfirmedAsync(
        string operation,
        string? confirmedEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Context.IsReadOnly)
            throw new PpdsException(EnvironmentConfirmationErrorCodes.ReadOnly,
                $"'{operation}' is disabled because this session is read-only.");

        if (string.IsNullOrWhiteSpace(Context.SessionId) ||
            string.IsNullOrWhiteSpace(Context.ProfileIdentity) ||
            string.IsNullOrWhiteSpace(Context.EnvironmentUrl))
            throw new PpdsException(EnvironmentConfirmationErrorCodes.Unresolved,
                "A resolved session, authenticated identity, and environment are required before writing.");

        var target = NormalizeUrl(Context.EnvironmentUrl);
        var supplied = confirmedEnvironment is not null;
        if (supplied && !string.Equals(target, NormalizeUrl(confirmedEnvironment!), StringComparison.Ordinal))
            throw new PpdsException(EnvironmentConfirmationErrorCodes.TargetMismatch,
                $"The acknowledgement does not match the resolved target '{target}'. No write was performed.");

        EnvironmentConfig? config;
        try
        {
            config = await _configuration.GetConfigAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(EnvironmentConfirmationErrorCodes.PolicyUnavailable,
                $"Could not resolve write protection for '{target}'. No write was performed.", ex);
        }

        var type = config?.Type ?? EnvironmentType.Unknown;
        var protection = WriteProtectionResolver.Resolve(type, config?.Protection);
        var policy = $"{type}|{protection}";
        var key = $"{Context.SessionId}\n{Context.ProfileIdentity}\n{target}";
        var acknowledged = _acknowledgements.Check(key, policy, supplied);
        if (protection is ProtectionLevel.Production or ProtectionLevel.Test && !acknowledged)
            throw new PpdsException(EnvironmentConfirmationErrorCodes.Required,
                $"Confirm '{operation}' against '{target}' (type: {type}, protection: {protection}) " +
                $"with --confirm-env {target} or confirmEnvironment before writing.");
    }

    /// <summary>Canonicalizes only harmless URL variations, without fuzzy environment matching.</summary>
    public static string NormalizeUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new PpdsException(EnvironmentConfirmationErrorCodes.InvalidUrl,
                "Environment acknowledgement must be an absolute organization URL without credentials, query, or fragment.");

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
            + uri.AbsolutePath.TrimEnd('/');
    }
}

/// <summary>Compatibility for directly constructed read services; missing context fails closed on writes.</summary>
internal sealed class MissingEnvironmentConfirmationService : IEnvironmentConfirmationService
{
    public static readonly MissingEnvironmentConfirmationService Instance = new();
    public IEnvironmentConfirmationContext Context { get; } = new EnvironmentConfirmationContext("", "", "");
    public Task EnsureConfirmedAsync(string operation, string? confirmedEnvironment = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PpdsException(EnvironmentConfirmationErrorCodes.Unresolved,
            "A resolved environment confirmation context is required before writing.");
    }
}

