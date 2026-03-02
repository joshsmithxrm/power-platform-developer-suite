namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Abstraction over platform-specific clipboard operations.
/// Enables testing without requiring real clipboard tools or terminal access.
/// </summary>
internal interface IClipboardBackend
{
    bool CopyViaProcess(string program, string args, string text);
    string? PasteViaProcess(string program, string args);
    bool CopyViaOsc52(string text);
    bool IsWindows { get; }
    bool IsMacOS { get; }
    bool IsWsl { get; }
    bool HasDisplayServer { get; }
}
