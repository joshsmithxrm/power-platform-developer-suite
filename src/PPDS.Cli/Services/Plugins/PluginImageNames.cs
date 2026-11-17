namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// The single rule for pairing a configured step image with a stored image on the same step.
/// Deploy (update-versus-create), image pruning, diff, the version 2.0 post-deploy verification and
/// the legacy preservation advisory all use it, so a name is never "the same image" to one of them
/// and "missing plus orphaned" to another.
/// </summary>
/// <remarks>
/// Names are compared ordinal case-insensitively. Deploy's write path locates the row to update with a
/// Dataverse <c>Equal</c> condition on <c>name</c>, which Dataverse evaluates under its default
/// case-insensitive collation, and then writes the configured name to that row. Treating a case-only
/// difference as the same image therefore matches what deploy actually does, keeps pruning on the
/// conservative side (a stored image whose name differs from a configured one only by case is never a
/// deletion candidate), and lets diff report the case difference as a <c>name</c> change on that image
/// rather than as a missing image plus an orphaned one. Two stored images, or two configured images,
/// whose names differ only by case are ambiguous and are never paired.
/// </remarks>
internal static class PluginImageNames
{
    /// <summary>Comparer for configured-to-stored image name pairing.</summary>
    internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>True when the two names identify the same image on a step.</summary>
    internal static bool Same(string? first, string? second) => Comparer.Equals(first, second);
}
