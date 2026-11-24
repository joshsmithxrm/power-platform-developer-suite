using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Query;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Single source of truth for resolving the effective <see cref="ProtectionLevel"/> of an environment
/// from its saved configuration, for every writer that gates on it, and for deciding whether a change
/// to that configuration lowers protection (issues #1195, #1706, #1829).
/// </summary>
/// <remarks>
/// <para>
/// Every writer uses one rule: an explicit per-environment protection value wins; otherwise the saved
/// type decides, and an environment with no saved type (no entry, an entry without a type, or type
/// Unknown) is Production. The environment-confirmation gate, <c>api request</c>, the model-driven-app
/// writers, SQL DML on the connected environment and cross-environment SQL DML all read it through
/// the helpers below.
/// </para>
/// <para>
/// Until #1829, SQL DML treated an entry without a type as Development, so saving only a label, adding
/// an environment in the extension, or the daemon tagging discovered environments silently switched
/// SQL DML confirmation off. Such a save now changes nothing; recording a weaker type is a lowering.
/// </para>
/// </remarks>
public static class WriteProtectionResolver
{
    /// <summary>
    /// Resolves the effective protection level from a type and an optional explicit protection value.
    /// Fail-safe: an unknown/undetectable environment type resolves to Production so mutating
    /// requests are blocked without confirmation. (<see cref="DmlSafetyGuard.DetectProtectionLevel"/> on
    /// its own maps Unknown to Development, which would fail open, so Unknown is resolved here first.)
    /// An explicit per-environment protection override always wins. A value that is not a defined member of
    /// <see cref="EnvironmentType"/> or <see cref="ProtectionLevel"/> (a number cast in, for example) is
    /// Production: it never resolves weaker than Production (#1829).
    /// </summary>
    public static ProtectionLevel Resolve(EnvironmentType envType, ProtectionLevel? configuredProtection)
    {
        if (configuredProtection is { } explicitProtection)
            return Enum.IsDefined(explicitProtection) ? explicitProtection : ProtectionLevel.Production;

        return envType == EnvironmentType.Unknown || !Enum.IsDefined(envType)
            ? ProtectionLevel.Production
            : DmlSafetyGuard.DetectProtectionLevel(envType);
    }

    /// <summary>
    /// Resolves the effective protection level for an environment by reading its (local) configuration.
    /// </summary>
    public static async Task<ProtectionLevel> ResolveAsync(
        IEnvironmentConfigService envConfigService,
        string environmentUrl,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envConfigService);

        var config = await envConfigService.GetConfigAsync(environmentUrl, ct);
        return ResolveForConfirmation(config);
    }

    /// <summary>
    /// Protection used by the environment-confirmation gate, <c>api request</c> and the model-driven-app
    /// writers: <see cref="Resolve"/> of the saved type (Unknown when there is no entry or no type).
    /// </summary>
    public static ProtectionLevel ResolveForConfirmation(EnvironmentConfig? config)
        => Resolve(config?.Type ?? EnvironmentType.Unknown, config?.Protection);

    /// <summary>
    /// Protection used by SQL DML against the connected environment: the same rule as
    /// <see cref="ResolveForConfirmation"/>. An environment with no saved type is Production, whether it
    /// has no entry or an entry without a type (#1829).
    /// </summary>
    public static ProtectionLevel ResolveForSqlDml(EnvironmentConfig? config) => ResolveForConfirmation(config);

    /// <summary>
    /// Protection of another environment named as the target of cross-environment SQL DML: the same rule
    /// as <see cref="ResolveForConfirmation"/> (type Unknown is Production too).
    /// </summary>
    public static ProtectionLevel ResolveForCrossEnvironmentTarget(EnvironmentConfig? config) => ResolveForConfirmation(config);

    /// <summary>
    /// Describes how the change from <paramref name="before"/> to <paramref name="after"/> weakens the
    /// environment's protection (Production, then Test, then Development), or returns null when it keeps
    /// or raises it. One rule serves every writer, so one comparison covers them all.
    /// </summary>
    /// <param name="before">The saved entry before the change; null when there is none.</param>
    /// <param name="after">The entry the change would save; null when it removes the entry.</param>
    /// <remarks>
    /// Creating an entry without a type and removing an entry never lower protection: no entry, an entry
    /// without a type and type Unknown are all Production.
    /// </remarks>
    public static ProtectionLowering? FindLowering(EnvironmentConfig? before, EnvironmentConfig? after)
    {
        var from = ResolveForConfirmation(before);
        var to = ResolveForConfirmation(after);

        // ProtectionLevel is declared weakest first: Development < Test < Production.
        return to < from ? new ProtectionLowering(from, to) : null;
    }
}

/// <summary>How a configuration change would weaken an environment's protection.</summary>
/// <param name="From">The protection before the change.</param>
/// <param name="To">The protection after the change.</param>
/// <remarks>
/// Every writer reads the same protection: the environment-confirmation gate (forms, views, flows),
/// model-driven apps, <c>api request</c>, SQL DML and cross-environment SQL DML.
/// </remarks>
public sealed record ProtectionLowering(ProtectionLevel From, ProtectionLevel To)
{
    /// <summary>Short description for messages, for example "Production to Development".</summary>
    public string Describe() => $"{From} to {To}";
}
