namespace PPDS.Cli.Tui.Testing.States;

public sealed record TabBarState(
    int TabCount,
    int ActiveIndex,
    IReadOnlyList<string> TabLabels,
    bool IsVisible);
