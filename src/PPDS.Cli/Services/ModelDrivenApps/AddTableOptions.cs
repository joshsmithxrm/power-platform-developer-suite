namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Options for the add-table operation.
/// </summary>
public sealed record AddTableOptions(
    string? Group,
    string? Area,
    string? Title,
    string? Solution,
    bool Publish,
    bool Confirm = false);
