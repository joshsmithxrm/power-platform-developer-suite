using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Whether a host may lower an environment's write protection at all (#1829). Registered once per host;
/// <see cref="EnvironmentConfigService"/> reads it.
/// </summary>
public sealed class ProtectionLoweringPolicy
{
    /// <summary>The CLI, the TUI and the daemon: lowering is saved only with a confirmation naming the environment.</summary>
    public static readonly ProtectionLoweringPolicy RequireConfirmation = new(allowsConfirmedLowering: true);

    /// <summary>The MCP server: lowering is refused, confirmed or not.</summary>
    public static readonly ProtectionLoweringPolicy Refuse = new(allowsConfirmedLowering: false);

    private ProtectionLoweringPolicy(bool allowsConfirmedLowering)
    {
        AllowsConfirmedLowering = allowsConfirmedLowering;
    }

    /// <summary>True when a confirmed lowering may be saved.</summary>
    public bool AllowsConfirmedLowering { get; }
}

/// <summary>What a configuration change would do to an environment's write protection.</summary>
/// <param name="EnvironmentUrl">The environment being changed, as the caller named it.</param>
/// <param name="CurrentType">The saved type before the change (null: none).</param>
/// <param name="NewType">The saved type after the change (null: none).</param>
/// <param name="Lowering">How the change weakens protection; null when it keeps or raises it.</param>
/// <param name="DiscoveredTypeWarning">Set when the new type is less protected than the type discovery reported.</param>
public sealed record ProtectionChangeAssessment(
    string EnvironmentUrl,
    EnvironmentType? CurrentType,
    EnvironmentType? NewType,
    ProtectionLowering? Lowering,
    string? DiscoveredTypeWarning)
{
    /// <summary>True when the change weakens protection.</summary>
    public bool LowersProtection => Lowering is not null;

    /// <summary>One sentence (two with a discovery warning) saying what the change lowers, for prompts and errors.</summary>
    public string Describe()
    {
        var change = NewType == CurrentType
            ? $"Saving configuration for '{EnvironmentUrl}'"
            : $"Changing the type of '{EnvironmentUrl}' from {CurrentType?.ToString() ?? "(none)"} to {NewType?.ToString() ?? "(none)"}";
        var lowering = Lowering is null ? "nothing" : Lowering.Describe();
        var text = $"{change} lowers write protection from {lowering} for forms, views, flows, apps, api request and SQL DML.";
        return DiscoveredTypeWarning is null ? text : $"{text} {DiscoveredTypeWarning}";
    }
}
