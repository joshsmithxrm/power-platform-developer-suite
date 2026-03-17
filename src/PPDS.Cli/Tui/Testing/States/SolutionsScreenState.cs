namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the SolutionsScreen for testing.
/// </summary>
/// <param name="SolutionCount">Number of solutions currently displayed.</param>
/// <param name="SelectedSolutionName">Friendly name of the selected solution.</param>
/// <param name="SelectedSolutionVersion">Version of the selected solution.</param>
/// <param name="SelectedIsManaged">Whether the selected solution is managed.</param>
/// <param name="ComponentCount">Number of components in the selected solution (null if no detail loaded).</param>
/// <param name="IsLoading">Whether data is currently loading.</param>
/// <param name="ShowManaged">Whether the "Include Managed" checkbox is checked.</param>
/// <param name="FilterText">Current text in the filter field.</param>
/// <param name="ErrorMessage">Error message if loading failed (null if no error).</param>
public sealed record SolutionsScreenState(
    int SolutionCount,
    string? SelectedSolutionName,
    string? SelectedSolutionVersion,
    bool? SelectedIsManaged,
    int? ComponentCount,
    bool IsLoading,
    bool ShowManaged,
    string? FilterText,
    string? ErrorMessage);
