// Pattern: TUI Panel with Terminal.Gui
// Demonstrates: Layout, keyboard nav, service injection, async updates
// Related: ADR-0028, CLAUDE.md "Implement logic in Application Services"
// Source: src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs

// KEY PRINCIPLES:
// 1. Use Pos.Bottom(), Dim.Fill() for responsive layouts
// 2. Marshal async updates to main thread with Application.MainLoop?.Invoke()
// 3. Logic in services - TUI is a dumb view
// 4. Color scheme from TuiColorPalette (centralized)

using Terminal.Gui;

public class DataExplorerPanel : View
{
    private readonly InteractiveSession _session;
    private readonly IDataExportService _exportService;

    private readonly FrameView _queryFrame;
    private readonly TextView _queryInput;
    private readonly FrameView _filterFrame;
    private readonly TableView _resultsTable;
    private readonly Label _statusLabel;

    public DataExplorerPanel(InteractiveSession session)
    {
        _session = session;
        _exportService = session.Services.GetRequiredService<IDataExportService>();

        // PATTERN: Fill entire container
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = TuiColorPalette.Default;

        // PATTERN: Fixed-height input area
        _queryFrame = new FrameView("Query (Ctrl+Enter to execute)")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 6  // Fixed height
        };

        _queryInput = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),   // Fill frame width
            Height = Dim.Fill()   // Fill frame height
        };
        _queryFrame.Add(_queryInput);

        // PATTERN: Toggleable filter (initially hidden)
        _filterFrame = new FrameView("Filter (/)")
        {
            X = 0,
            Y = Pos.Bottom(_queryFrame),  // Relative positioning
            Width = Dim.Fill(),
            Height = 3,
            Visible = false  // Toggle with '/'
        };

        // PATTERN: Results fill remaining space
        _resultsTable = new TableView
        {
            X = 0,
            Y = Pos.Bottom(_queryFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2  // Reserve space for status bar
        };

        // PATTERN: Status bar anchored to bottom
        _statusLabel = new Label("Ready...")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),  // 1 line from bottom
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = TuiColorPalette.StatusBar_Default
        };

        Add(_queryFrame, _filterFrame, _resultsTable, _statusLabel);

        SetupKeyboardHandlers();
        SetupEventSubscriptions();
    }

    private void SetupKeyboardHandlers()
    {
        // PATTERN: Handle keyboard shortcuts without blocking
        _queryInput.KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == (Key.CtrlMask | Key.Enter))
            {
                // Fire-and-forget with proper error handling
                _ = ExecuteQueryAsync();
                e.Handled = true;
            }
        };

        // PATTERN: Global shortcuts for the panel
        KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.Slash:
                    ToggleFilter();
                    e.Handled = true;
                    break;

                case Key.Esc when _filterFrame.Visible:
                    _filterFrame.Visible = false;
                    AdjustResultsPosition();
                    e.Handled = true;
                    break;
            }
        };
    }

    private void SetupEventSubscriptions()
    {
        // PATTERN: React to session-level changes
        _session.EnvironmentChanged += OnEnvironmentChanged;
    }

    private void OnEnvironmentChanged(string? url, string? displayName)
    {
        // PATTERN: Marshal async updates to main thread
        Application.MainLoop?.Invoke(() =>
        {
            _statusLabel.Text = $"Connected to: {displayName ?? url ?? "None"}";
            _statusLabel.ColorScheme = url != null
                ? TuiColorPalette.StatusBar_Connected
                : TuiColorPalette.StatusBar_Disconnected;

            // Clear stale data on environment change
            _resultsTable.Table = null;
        });
    }

    private async Task ExecuteQueryAsync()
    {
        var query = _queryInput.Text.ToString();
        if (string.IsNullOrWhiteSpace(query))
            return;

        // PATTERN: Update status immediately
        Application.MainLoop?.Invoke(() =>
        {
            _statusLabel.Text = "Executing query...";
            _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Working;
        });

        try
        {
            // PATTERN: Logic in service, not in TUI
            var result = await _exportService.QueryAsync(query, CancellationToken.None);

            // PATTERN: Update UI on main thread
            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.Table = ConvertToDataTable(result.Records);
                _statusLabel.Text = $"{result.Records.Count} records returned";
                _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Success;
            });
        }
        catch (PpdsException ex)
        {
            // PATTERN: Show user-friendly error message
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Error: {ex.UserMessage}";
                _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Error;
            });
        }
    }

    private void ToggleFilter()
    {
        _filterFrame.Visible = !_filterFrame.Visible;
        AdjustResultsPosition();

        if (_filterFrame.Visible)
        {
            _filterFrame.SetFocus();
        }
    }

    private void AdjustResultsPosition()
    {
        // PATTERN: Adjust layout based on visibility
        _resultsTable.Y = _filterFrame.Visible
            ? Pos.Bottom(_filterFrame)
            : Pos.Bottom(_queryFrame);
    }

    protected override void Dispose(bool disposing)
    {
        // PATTERN: Unsubscribe from events
        _session.EnvironmentChanged -= OnEnvironmentChanged;
        base.Dispose(disposing);
    }
}

// COLOR PALETTE: Centralized theming
public static class TuiColorPalette
{
    public static ColorScheme Default => new()
    {
        Normal = new Attribute(Color.White, Color.Black),
        Focus = new Attribute(Color.Black, Color.Cyan),
        HotNormal = new Attribute(Color.BrightCyan, Color.Black),
        HotFocus = new Attribute(Color.BrightCyan, Color.Cyan)
    };

    public static ColorScheme StatusBar_Default => CreateStatus(Color.Gray, Color.Black);
    public static ColorScheme StatusBar_Connected => CreateStatus(Color.Green, Color.Black);
    public static ColorScheme StatusBar_Disconnected => CreateStatus(Color.Red, Color.Black);
    public static ColorScheme StatusBar_Working => CreateStatus(Color.BrightYellow, Color.Black);
    public static ColorScheme StatusBar_Success => CreateStatus(Color.BrightGreen, Color.Black);
    public static ColorScheme StatusBar_Error => CreateStatus(Color.BrightRed, Color.Black);

    private static ColorScheme CreateStatus(Color fg, Color bg) => new()
    {
        Normal = new Attribute(fg, bg)
    };
}

// ANTI-PATTERNS TO AVOID:
//
// BAD: Absolute positioning
// _resultsTable.X = 10;
// _resultsTable.Y = 5;  // WRONG - doesn't adapt to resize
//
// BAD: Updating UI from background thread
// await Task.Run(() => {
//     _statusLabel.Text = "Done";  // WRONG - must use MainLoop.Invoke
// });
//
// BAD: Business logic in TUI
// private async Task ExecuteQueryAsync() {
//     var client = await _pool.GetClientAsync();  // WRONG - use service
//     var results = await client.QueryAsync(...);
// }
//
// BAD: Hardcoded colors
// _statusLabel.ColorScheme = new ColorScheme { ... };  // WRONG - use palette
