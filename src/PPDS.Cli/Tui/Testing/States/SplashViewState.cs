namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// State capture for splash view testing.
/// </summary>
public sealed record SplashViewState(
    string StatusMessage,
    bool IsReady,
    string? Version,
    bool SpinnerActive);
