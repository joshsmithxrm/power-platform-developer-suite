using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>Immutable target identity shared by the guard and the operation's connection pool.</summary>
public interface IEnvironmentConfirmationContext
{
    string SessionId { get; }
    string ProfileIdentity { get; }
    string EnvironmentUrl { get; }
    bool IsReadOnly { get; }
}

/// <summary>Captured operation context. An absent target never authorizes writing.</summary>
public sealed record EnvironmentConfirmationContext(
    string SessionId,
    string ProfileIdentity,
    string EnvironmentUrl,
    bool IsReadOnly = false) : IEnvironmentConfirmationContext
{
    public static EnvironmentConfirmationContext FromProfile(
        AuthProfile profile, string environmentUrl, string? sessionId = null, bool isReadOnly = false)
        => new(sessionId ?? Guid.NewGuid().ToString("N"),
            string.Join("|", profile.Index, profile.CreatedAt.ToString("O"), profile.AuthMethod,
                profile.TenantId, profile.ObjectId, profile.HomeAccountId, profile.ApplicationId,
                profile.Username),
            environmentUrl, isReadOnly);
}

/// <summary>Service-layer first-write acknowledgement, independent of surface permissions.</summary>
public interface IEnvironmentConfirmationService
{
    IEnvironmentConfirmationContext Context { get; }

    Task EnsureConfirmedAsync(
        string operation,
        string? confirmedEnvironment = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable codes for target acknowledgement failures.</summary>
public static class EnvironmentConfirmationErrorCodes
{
    public const string Required = "Environment.ConfirmationRequired";
    public const string TargetMismatch = "Environment.ConfirmationTargetMismatch";
    public const string ReadOnly = "Environment.ReadOnly";
    public const string Unresolved = "Environment.TargetUnresolved";
    public const string InvalidUrl = "Environment.InvalidConfirmationUrl";
    public const string PolicyUnavailable = "Environment.ConfirmationPolicyUnavailable";
}

