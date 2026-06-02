namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Summary of a model-driven app for list operations.
/// </summary>
public sealed record ModelDrivenAppSummary(
    Guid AppModuleId,
    string DisplayName,
    string UniqueName,
    int ComponentCount);
