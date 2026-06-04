namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Options for set-forms, set-views, and set-charts operations.
/// </summary>
public sealed record ComponentSelectionOptions(
    bool All,
    IReadOnlyList<string> ComponentNames,
    string? Solution,
    bool Publish);
