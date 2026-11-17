using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// The rule for pairing a configured step image with a stored image on the same step. Deploy
/// (update-versus-create counting), image pruning, diff, the version 2.0 post-deploy verification,
/// the legacy preservation advisory and extraction merge all use it, so a case-only difference is
/// never "the same image" to one of them and "missing plus orphaned" to another.
/// </summary>
/// <remarks>
/// <para>
/// Names are compared ordinal case-insensitively. Deploy's write path locates the row to update with a
/// Dataverse <c>Equal</c> condition on <c>name</c>; Dataverse string conditions are case-insensitive by
/// default, so the write already treats a case-only difference as the same row, and then writes the
/// configured name to it. Pairing case-only differences here therefore matches the write, keeps pruning
/// on the conservative side (a stored image whose name differs from a configured one only by case is
/// never a deletion candidate) and lets diff report the difference as a <c>name</c> change on that image.
/// </para>
/// <para>
/// Only case is covered. If the environment's collation also equates other differences (for example
/// accents or trailing spaces), the write may update a row this rule does not pair: diff then reports a
/// missing plus an orphaned image and pruning may plan the stored row for deletion. That is not
/// established for Dataverse here and is a known unpaired case.
/// </para>
/// <para>
/// Configured names on one step that collide under this rule are rejected before any connection
/// (<see cref="EnsureUniqueConfigured"/>). Stored names that collide with a configured name cannot be
/// paired: diff reports them as incomplete and pruning refuses its plan. Only the write path must still
/// choose a row when several stored rows match; it prefers the exact-case row, otherwise the earliest
/// created (then lowest ID), and writes the configured name to it.
/// </para>
/// </remarks>
internal static class PluginImageNames
{
    /// <summary>Comparer for configured-to-stored image name pairing.</summary>
    internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>True when the two names identify the same image on a step.</summary>
    internal static bool Same(string? first, string? second) => Comparer.Equals(first, second);

    /// <summary>
    /// Rejects a file whose step configures image names that are equal under <see cref="Comparer"/>:
    /// every such entry would be written to the same stored image.
    /// </summary>
    internal static void EnsureUniqueConfigured(PluginAssemblyConfig assembly)
    {
        foreach (var type in assembly.Types)
        foreach (var step in type.Steps)
        {
            if (step.Images is null) continue;
            var collisions = step.Images.Where(i => !string.IsNullOrWhiteSpace(i?.Name))
                .GroupBy(i => i.Name, Comparer).Where(g => g.Count() > 1).ToList();
            if (collisions.Count == 0) continue;
            var names = string.Join("; ", collisions.Select(g => string.Join(", ", g.Select(i => $"'{i.Name}'"))));
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                $"Step '{PluginStepMatcher.ResolveConfigName(type, step)}' (type '{type.TypeName}', assembly '{assembly.Name}') " +
                $"configures image names that are equal ignoring case: {names}. Image names must be unique on a step " +
                "ignoring case, because every such entry would be written to the same stored image. " +
                "Rename or remove the duplicates in the registration file.");
        }
    }
}
