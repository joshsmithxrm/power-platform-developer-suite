using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// TUI adapter for <see cref="IMetadataAuthoringProgressReporter"/> that updates a status label.
/// </summary>
internal sealed class TuiMetadataAuthoringProgressReporter : IMetadataAuthoringProgressReporter
{
    private readonly Label _statusLabel;

    public TuiMetadataAuthoringProgressReporter(Label statusLabel)
    {
        _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
    }

    public void ReportPhase(string phase, string? detail = null)
    {
        var text = detail != null ? $"{phase}: {detail}" : phase;
        Application.MainLoop?.Invoke(() => _statusLabel.Text = text);
    }

    public void ReportInfo(string message)
    {
        Application.MainLoop?.Invoke(() => _statusLabel.Text = message);
    }
}
