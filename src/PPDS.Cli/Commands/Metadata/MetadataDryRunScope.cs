using PPDS.Cli.Infrastructure.Output;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>Reports completed checks without implying that the platform accepted a write.</summary>
internal sealed record MetadataDryRunScope(string ValidationScope, bool PublisherPrefixChecked,
    bool TargetExistenceChecked, string OptionValueAllocation)
{
    internal static MetadataDryRunScope Inputs { get; } = new("required-inputs", false, false, "not-performed");
    internal static MetadataDryRunScope Publisher { get; } = new("input-and-publisher", true, false, "not-performed");
    internal static MetadataDryRunScope PublisherProvisional { get; } = new("input-and-publisher", true, false, "provisional");
    internal static MetadataDryRunScope Option { get; } = new("option-existence", false, true, "not-performed");
    internal static MetadataDryRunScope DraftValue { get; } = new("option-data-and-draft-value", false, false, "provisional");
    internal static MetadataDryRunScope RequestedValue { get; } = new("required-inputs", false, false, "requested-only; SDK assignment occurs on write");

    public bool DependenciesChecked => false;
    public bool ServerFeasibilityChecked => false;
    public bool ChangesPersisted => false;
    public bool TargetOptionsRead => ValidationScope is "option-existence" or "option-data-and-draft-value";

    internal void WriteSuccess<T>(IOutputWriter writer, bool dryRun, T data)
    {
        if (dryRun && writer is JsonOutputWriter json)
            json.WriteSuccess(data, new { preview = this });
        else
            writer.WriteSuccess(data);
    }

    internal void WriteDiagnostic() => Console.Error.WriteLine(
        $"[Dry-Run] Checks: {ValidationScope}. Target existence {(TargetExistenceChecked ? "checked" : "not checked")}. " +
        $"Dependencies and server feasibility not checked. Option value allocation: {OptionValueAllocation}. No changes persisted.");
}
