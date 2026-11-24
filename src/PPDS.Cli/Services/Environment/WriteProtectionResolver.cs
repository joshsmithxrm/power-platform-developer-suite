using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Query;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Single source of truth for resolving the effective <see cref="ProtectionLevel"/> of an environment
/// from its saved configuration, for every writer that gates on it, and for deciding whether a change
/// to that configuration lowers protection (issues #1195, #1829).
/// </summary>
/// <remarks>
/// <para>
/// Three writers read protection today, with different rules for a missing type (#1706 tracks
/// unifying them). Each is resolved here so that the rule for lowering protection
/// (<see cref="FindLowering"/>) is judged against all three:
/// </para>
/// <list type="bullet">
/// <item><see cref="ResolveForConfirmation"/>: the environment-confirmation gate, <c>api request</c> and the
/// model-driven-app writers. A missing type or entry is Production.</item>
/// <item><see cref="ResolveForSqlDml"/>: SQL DML against the connected environment. No entry is Production;
/// an entry without a type is Development.</item>
/// <item><see cref="ResolveForCrossEnvironmentTarget"/>: SQL DML against another environment by label.
/// A missing type is Production.</item>
/// </list>
/// <para>An explicit per-environment protection value always wins.</para>
/// </remarks>
public static class WriteProtectionResolver
{
    /// <summary>
    /// Resolves the effective protection level for the write guard.
    /// Fail-safe: an unknown/undetectable environment type resolves to Production so mutating
    /// requests are blocked without --confirm. (<see cref="DmlSafetyGuard.DetectProtectionLevel"/> maps
    /// Unknown to Development for SQL DML, which would fail open here — so we resolve Unknown locally.)
    /// An explicit per-environment protection override always wins.
    /// </summary>
    public static ProtectionLevel Resolve(EnvironmentType envType, ProtectionLevel? configuredProtection)
        => configuredProtection
            ?? (envType == EnvironmentType.Unknown
                ? ProtectionLevel.Production
                : DmlSafetyGuard.DetectProtectionLevel(envType));

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
    /// Protection used by SQL DML against the connected environment. With no saved entry the query
    /// service keeps its Production default; with an entry, an explicit protection wins and otherwise
    /// the saved type is mapped by <see cref="DmlSafetyGuard.DetectProtectionLevel"/> (no type: Development).
    /// </summary>
    public static ProtectionLevel ResolveForSqlDml(EnvironmentConfig? config)
        => config is null
            ? ProtectionLevel.Production
            : config.Protection ?? DmlSafetyGuard.DetectProtectionLevel(config.Type ?? EnvironmentType.Unknown);

    /// <summary>
    /// Protection of another environment named as the target of cross-environment SQL DML. A missing
    /// entry or type is Production.
    /// </summary>
    public static ProtectionLevel ResolveForCrossEnvironmentTarget(EnvironmentConfig? config)
        => config?.Protection ?? DmlSafetyGuard.DetectProtectionLevel(config?.Type ?? EnvironmentType.Production);

    /// <summary>
    /// Lists every writer whose protection the change from <paramref name="before"/> to
    /// <paramref name="after"/> makes weaker (Production, then Test, then Development). Empty when the
    /// change keeps or raises protection for every writer.
    /// </summary>
    /// <param name="before">The saved entry before the change; null when there is none.</param>
    /// <param name="after">The entry the change would save; null when it removes the entry.</param>
    /// <remarks>
    /// The confirmation-gate rule is always compared. The two SQL rules are compared only when an entry
    /// existed before the change: creating an entry without a type moves SQL DML on that environment
    /// from Production to Development through the SQL rule's own treatment of a missing type, which is
    /// #1706 and not a choice the caller made. Removing an entry never lowers protection: every writer
    /// treats a missing entry as Production.
    /// </remarks>
    public static IReadOnlyList<ProtectionLowering> FindLowering(EnvironmentConfig? before, EnvironmentConfig? after)
    {
        var lowered = new List<ProtectionLowering>();
        AddIfLowered(lowered, ProtectionWriter.WriteConfirmation,
            ResolveForConfirmation(before), ResolveForConfirmation(after));

        if (before is not null && after is not null)
        {
            AddIfLowered(lowered, ProtectionWriter.SqlDml,
                ResolveForSqlDml(before), ResolveForSqlDml(after));
            AddIfLowered(lowered, ProtectionWriter.CrossEnvironmentSqlDml,
                ResolveForCrossEnvironmentTarget(before), ResolveForCrossEnvironmentTarget(after));
        }

        return lowered;
    }

    private static void AddIfLowered(
        List<ProtectionLowering> lowered, ProtectionWriter writer, ProtectionLevel from, ProtectionLevel to)
    {
        // ProtectionLevel is declared weakest first: Development < Test < Production.
        if (to < from)
            lowered.Add(new ProtectionLowering(writer, from, to));
    }
}

/// <summary>A writer that gates on an environment's saved protection.</summary>
public enum ProtectionWriter
{
    /// <summary>The environment-confirmation gate (forms, views, flows), <c>api request</c> and model-driven apps.</summary>
    WriteConfirmation,

    /// <summary>SQL DML against the connected environment.</summary>
    SqlDml,

    /// <summary>SQL DML against another environment named by its label.</summary>
    CrossEnvironmentSqlDml
}

/// <summary>One writer whose protection a configuration change would weaken.</summary>
/// <param name="Writer">The writer affected.</param>
/// <param name="From">Its protection before the change.</param>
/// <param name="To">Its protection after the change.</param>
public sealed record ProtectionLowering(ProtectionWriter Writer, ProtectionLevel From, ProtectionLevel To)
{
    /// <summary>Short description for messages, for example "write confirmation: Production to Development".</summary>
    public string Describe() => $"{WriterName(Writer)}: {From} to {To}";

    private static string WriterName(ProtectionWriter writer) => writer switch
    {
        ProtectionWriter.WriteConfirmation => "write confirmation (forms, views, flows, apps, api request)",
        ProtectionWriter.SqlDml => "SQL DML",
        ProtectionWriter.CrossEnvironmentSqlDml => "cross-environment SQL DML",
        _ => writer.ToString()
    };
}
