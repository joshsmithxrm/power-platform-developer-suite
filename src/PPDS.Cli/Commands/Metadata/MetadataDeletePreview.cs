using System.Text.Json;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Describes the limited checks performed by the existing delete preview paths.
/// These presentation values are emitted only after the authoring service succeeds.
/// </summary>
internal sealed class MetadataDeletePreview
{
    internal const string InputOnlyHelp = "Validate required inputs only. Target existence, dependencies and deletion feasibility are not checked. No changes persisted.";
    internal const string OptionExistenceHelp = "Validate target option existence only. Dependencies and deletion feasibility are not checked. No changes persisted.";
    internal const string DependenciesHelp = "Read target metadata and deletion dependencies without deleting. Blocking components or incomplete checks return nonzero; other server constraints remain unchecked.";

    internal static Task<MetadataDeletePreviewResult> InspectDependenciesAsync(IMetadataAuthoringService service, MetadataDeleteTarget target, CancellationToken ct)
        => service is IMetadataDeletePreviewService preview
            ? preview.PreviewDeleteAsync(target, ct)
            : throw new PpdsException(ErrorCodes.Operation.NotSupported, "This authoring service cannot inspect deletion dependencies; no dependency-checked preview is available.");

    internal static int WriteDependencies<T>(IOutputWriter writer, T data, MetadataDeletePreviewResult preview)
    {
        var error = preview.IsClear ? null : StructuredError.Create(preview.Refusal().ErrorCode, preview.Refusal().UserMessage);
        if (writer.IsJsonMode)
            writer.WriteResult(new CommandResult<T> { Success = preview.IsClear, Data = data, Error = error });
        else
        {
            Console.Error.WriteLine(preview.IsClear
                ? "[Preview] No blocking dependencies found. Other server constraints remain unchecked. No changes persisted."
                : $"[Preview] {preview.Status}: {preview.DistinctComponentCount} component(s), {preview.DependencyRowCount} dependency row(s). No changes persisted.");
            foreach (var row in preview.BlockingComponents)
                Console.Error.WriteLine($"  Type {row.ComponentType?.ToString() ?? "unknown"}, object {row.ObjectId?.ToString() ?? "unknown"}, name {JsonSerializer.Serialize(row.Name)} ({row.NameResolutionStatus})");
            foreach (var warning in preview.Warnings) Console.Error.WriteLine("  " + warning);
            foreach (var issue in preview.NameResolutionIssues)
                Console.Error.WriteLine($"  Name lookup: {issue.ErrorCode}; reauthentication required: {issue.RequiresReauthentication}.");
        }
        return preview.IsClear ? ExitCodes.Success : preview.Status == "blocked" ? ExitCodes.PreconditionFailed : ExitCodes.Failure;
    }

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
