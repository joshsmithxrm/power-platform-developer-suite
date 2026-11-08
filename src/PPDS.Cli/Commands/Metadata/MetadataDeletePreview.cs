namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Describes the limited checks performed by the existing delete preview paths.
/// These presentation values are emitted only after the authoring service succeeds.
/// </summary>
internal sealed class MetadataDeletePreview
{
    internal const string InputOnlyHelp = "Validate required inputs only. Target existence, dependencies and deletion feasibility are not checked. No changes persisted.";
    internal const string OptionExistenceHelp = "Validate target option existence only. Dependencies and deletion feasibility are not checked. No changes persisted.";

    internal static MetadataDeletePreview InputOnly { get; } = new("input-only", false);
    internal static MetadataDeletePreview OptionExistence { get; } = new("option-existence", true);

    private MetadataDeletePreview(string validationScope, bool targetExistenceChecked)
    {
        ValidationScope = validationScope;
        TargetExistenceChecked = targetExistenceChecked;
    }

    public string ValidationScope { get; }
    public bool TargetExistenceChecked { get; }
    public bool DependenciesChecked => false;
    public bool DeletionFeasibilityChecked => false;

    internal void WriteDiagnostic()
    {
        Console.Error.WriteLine(TargetExistenceChecked
            ? "[Dry-Run] Option exists. Dependencies and deletion feasibility are not checked. No changes persisted."
            : "[Dry-Run] Input validation only. Target existence, dependencies and deletion feasibility are not checked. No changes persisted.");
    }
}
