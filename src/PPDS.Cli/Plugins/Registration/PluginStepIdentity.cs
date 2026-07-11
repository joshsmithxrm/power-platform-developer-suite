using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// The functional identity of a plugin processing step — the tuple of immutable coordinates
/// that determines which real Dataverse behavior a step represents, independent of its mutable
/// display name.
/// </summary>
/// <remarks>
/// <para>
/// A Dataverse step <c>name</c> is a mutable display label; only the <c>SdkMessageProcessingStepId</c>
/// GUID is unique. Two steps can legitimately share a name — commonly when the Plugin Registration
/// Tool auto-names steps <c>"{TypeName}: {Message} of {Entity}"</c> (omitting stage and mode), so a
/// PreOperation/PostOperation pair on the same message and entity collide on name. Matching
/// configuration to the environment by name therefore either mis-classifies steps or aborts. Keying
/// on this functional identity instead is stable across renames and unambiguous for distinct
/// behaviors.
/// </para>
/// <para>
/// Rank/ExecutionOrder and FilteringAttributes are deliberately NOT part of the identity: they are
/// mutable properties that deploy updates in place. Keying on them would turn a rank tweak into a
/// delete+create, breaking idempotent redeploys. Name is likewise NOT part of the identity.
/// </para>
/// </remarks>
public readonly record struct PluginStepIdentity(
    string PluginTypeName,
    string Message,
    string PrimaryEntity,
    string SecondaryEntity,
    string Stage,
    string Mode)
{
    /// <summary>Sentinel for an absent primary/secondary entity (global messages).</summary>
    public const string NoEntity = "none";

    /// <summary>
    /// Builds an identity from a configured step under the given plugin type.
    /// </summary>
    public static PluginStepIdentity FromConfig(string pluginTypeName, PluginStepConfig step) =>
        Normalize(pluginTypeName, step.Message, step.Entity, step.SecondaryEntity, step.Stage, step.Mode);

    /// <summary>
    /// Builds an identity from an existing environment step under the given plugin type.
    /// </summary>
    public static PluginStepIdentity FromEnvironment(string pluginTypeName, PluginStepInfo step) =>
        Normalize(pluginTypeName, step.Message, step.PrimaryEntity, step.SecondaryEntity, step.Stage, step.Mode);

    /// <summary>
    /// The single normalizer both factories funnel through: trims and lower-cases every component so
    /// casing/whitespace differences between config and environment never split an identity, and
    /// collapses null/empty/"none" entities to <see cref="NoEntity"/>.
    /// </summary>
    private static PluginStepIdentity Normalize(
        string? pluginTypeName,
        string? message,
        string? primaryEntity,
        string? secondaryEntity,
        string? stage,
        string? mode) =>
        new(
            NormalizeComponent(pluginTypeName),
            NormalizeComponent(message),
            NormalizeEntity(primaryEntity),
            NormalizeEntity(secondaryEntity),
            NormalizeComponent(stage),
            NormalizeComponent(mode));

    private static string NormalizeComponent(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeEntity(string? value)
    {
        var normalized = NormalizeComponent(value);
        return normalized.Length == 0 || normalized == NoEntity ? NoEntity : normalized;
    }

    /// <summary>
    /// Human-readable rendering for warnings and drift reporting, e.g.
    /// <c>myplugin.type: update of account (postoperation, synchronous)</c>. Components are shown in
    /// their normalized (lower-cased) form because that is what identity is compared on.
    /// </summary>
    public override string ToString()
    {
        var entity = SecondaryEntity == NoEntity
            ? PrimaryEntity
            : $"{PrimaryEntity} -> {SecondaryEntity}";
        return $"{PluginTypeName}: {Message} of {entity} ({Stage}, {Mode})";
    }
}

/// <summary>
/// Resolution for how <see cref="IPluginRegistrationService.UpsertStepAsync"/> should locate the row
/// to write when identity-based matching has already been performed by the caller.
/// </summary>
/// <param name="ExistingStepId">
/// The GUID of the environment step this configured step was matched to, or <c>null</c> to force a
/// create (the matcher found no counterpart). A non-null value updates exactly that row; a null value
/// skips the lookup entirely so a same-named-but-different-identity row can never be hijacked.
/// </param>
public sealed record StepIdentityResolution(Guid? ExistingStepId);

/// <summary>
/// Matches configured plugin steps to existing environment steps by functional identity
/// (<see cref="PluginStepIdentity"/>) rather than by mutable display name.
/// </summary>
/// <remarks>
/// Both sides are grouped by identity. Within each identity group both sides are ordered
/// deterministically and zipped into pairs; a surplus on the configured side becomes
/// <see cref="StepMatch.IsMissing"/> (needs create) and a surplus on the environment side becomes
/// <see cref="StepMatch.IsOrphaned"/> (candidate for --clean). Whenever either side of a group holds
/// more than one member the optional <c>onResidualCollision</c> callback fires: the zip still
/// converges (extras only ever add or delete), so a residual collision warns rather than aborts.
/// </remarks>
public static class PluginStepMatcher
{
    /// <summary>
    /// The pairing of at most one configured step and at most one environment step that share a
    /// functional identity.
    /// </summary>
    /// <remarks>
    /// Named <c>StepMatch</c> (not <c>Match</c>) to avoid colliding with the <see cref="Match"/> entry
    /// method on the enclosing type.
    /// </remarks>
    public sealed record StepMatch(
        PluginTypeConfig? TypeConfig,
        PluginStepConfig? Config,
        PluginTypeInfo? EnvType,
        PluginStepInfo? Env)
    {
        /// <summary>Both a configured step and an environment step are present (update in place).</summary>
        public bool IsPaired => Config is not null && Env is not null;

        /// <summary>Configured but absent from the environment (needs create).</summary>
        public bool IsMissing => Config is not null && Env is null;

        /// <summary>Present in the environment but not configured (orphan; delete under --clean).</summary>
        public bool IsOrphaned => Config is null && Env is not null;
    }

    /// <summary>
    /// Pairs configured steps with existing environment steps by functional identity.
    /// </summary>
    /// <param name="configured">Configured (type, step) pairs.</param>
    /// <param name="existing">Existing environment (type, step) pairs.</param>
    /// <param name="onResidualCollision">
    /// Invoked as <c>(identity, configuredCount, environmentCount)</c> whenever an identity group has
    /// more than one member on either side. The classification still proceeds.
    /// </param>
    public static IReadOnlyList<StepMatch> Match(
        IEnumerable<(PluginTypeConfig Type, PluginStepConfig Step)> configured,
        IEnumerable<(PluginTypeInfo Type, PluginStepInfo Step)> existing,
        Action<PluginStepIdentity, int, int>? onResidualCollision = null)
    {
        var configByIdentity = configured
            .GroupBy(c => PluginStepIdentity.FromConfig(c.Type.TypeName, c.Step))
            .ToDictionary(g => g.Key, g => g.ToList());

        var envByIdentity = existing
            .GroupBy(e => PluginStepIdentity.FromEnvironment(e.Type.TypeName, e.Step))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Deterministic set of identities: configured first (config order preserved by GroupBy),
        // then any environment-only identities.
        var identities = new List<PluginStepIdentity>();
        var seen = new HashSet<PluginStepIdentity>();
        foreach (var identity in configByIdentity.Keys.Concat(envByIdentity.Keys))
        {
            if (seen.Add(identity))
                identities.Add(identity);
        }

        var results = new List<StepMatch>();

        foreach (var identity in identities)
        {
            var configList = configByIdentity.TryGetValue(identity, out var cfg)
                ? cfg
                : new List<(PluginTypeConfig Type, PluginStepConfig Step)>();
            var envList = envByIdentity.TryGetValue(identity, out var env)
                ? env
                : new List<(PluginTypeInfo Type, PluginStepInfo Step)>();

            if (configList.Count > 1 || envList.Count > 1)
                onResidualCollision?.Invoke(identity, configList.Count, envList.Count);

            configList.Sort(CompareConfig);
            envList.Sort(CompareEnv);

            var max = Math.Max(configList.Count, envList.Count);
            for (var i = 0; i < max; i++)
            {
                PluginTypeConfig? typeConfig = null;
                PluginStepConfig? stepConfig = null;
                PluginTypeInfo? envType = null;
                PluginStepInfo? envStep = null;

                if (i < configList.Count)
                {
                    typeConfig = configList[i].Type;
                    stepConfig = configList[i].Step;
                }

                if (i < envList.Count)
                {
                    envType = envList[i].Type;
                    envStep = envList[i].Step;
                }

                results.Add(new StepMatch(typeConfig, stepConfig, envType, envStep));
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves the display name a configured step deploys as: its explicit <c>Name</c>, or the
    /// auto-name convention <c>"{TypeName}: {Message} of {Entity}"</c> when none is set. Kept identical
    /// to the convention used by <c>PluginRegistrationConfig.Validate()</c>.
    /// </summary>
    public static string ResolveConfigName(PluginTypeConfig typeConfig, PluginStepConfig stepConfig) =>
        stepConfig.Name ?? $"{typeConfig.TypeName}: {stepConfig.Message} of {stepConfig.Entity}";

    /// <summary>
    /// Formats a consistent warning describing a residual identity collision for surfacing to users.
    /// </summary>
    public static string DescribeResidualCollision(PluginStepIdentity identity, int configuredCount, int environmentCount) =>
        $"Multiple plugin steps share the functional identity [{identity}] " +
        $"(configured: {configuredCount}, environment: {environmentCount}). " +
        "They were paired positionally and any surplus is treated as add/remove; " +
        "give them distinct stage/mode/message/entity, or delete the redundant environment step, to disambiguate.";

    private static int CompareConfig(
        (PluginTypeConfig Type, PluginStepConfig Step) a,
        (PluginTypeConfig Type, PluginStepConfig Step) b)
    {
        var byOrder = a.Step.ExecutionOrder.CompareTo(b.Step.ExecutionOrder);
        if (byOrder != 0)
            return byOrder;

        var byFiltering = string.CompareOrdinal(
            NormalizeFiltering(a.Step.FilteringAttributes),
            NormalizeFiltering(b.Step.FilteringAttributes));
        if (byFiltering != 0)
            return byFiltering;

        return string.CompareOrdinal(ResolveConfigName(a.Type, a.Step), ResolveConfigName(b.Type, b.Step));
    }

    private static int CompareEnv(
        (PluginTypeInfo Type, PluginStepInfo Step) a,
        (PluginTypeInfo Type, PluginStepInfo Step) b)
    {
        var byOrder = a.Step.ExecutionOrder.CompareTo(b.Step.ExecutionOrder);
        if (byOrder != 0)
            return byOrder;

        var byFiltering = string.CompareOrdinal(
            NormalizeFiltering(a.Step.FilteringAttributes),
            NormalizeFiltering(b.Step.FilteringAttributes));
        if (byFiltering != 0)
            return byFiltering;

        var byName = string.CompareOrdinal(a.Step.Name ?? string.Empty, b.Step.Name ?? string.Empty);
        if (byName != 0)
            return byName;

        return a.Step.Id.CompareTo(b.Step.Id);
    }

    /// <summary>
    /// Normalizes a comma-separated filtering-attributes string for stable ordering: trims, lower-cases,
    /// drops empties, and sorts the tokens.
    /// </summary>
    private static string NormalizeFiltering(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes))
            return string.Empty;

        return string.Join(
            ",",
            attributes.Split(',')
                .Select(a => a.Trim().ToLowerInvariant())
                .Where(a => a.Length > 0)
                .OrderBy(a => a, StringComparer.Ordinal));
    }
}
