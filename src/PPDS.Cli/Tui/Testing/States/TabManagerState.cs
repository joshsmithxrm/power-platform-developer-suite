using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

public sealed record TabManagerState(
    int TabCount,
    int ActiveIndex,
    IReadOnlyList<TabSummary> Tabs);

public sealed record TabSummary(
    string ScreenType,
    string Title,
    string? EnvironmentUrl,
    EnvironmentType EnvironmentType,
    bool IsActive);
