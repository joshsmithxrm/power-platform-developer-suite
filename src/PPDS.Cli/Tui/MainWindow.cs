using PPDS.Auth.Credentials;
using PPDS.Cli.Interactive;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;

namespace PPDS.Cli.Tui;

/// <summary>
/// Main window containing the menu and navigation for the TUI application.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly string? _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;

    public MainWindow(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;

        Title = "PPDS - Power Platform Developer Suite";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        SetupMenu();
        ShowMainMenu();
    }

    private void SetupMenu()
    {
        var menu = new MenuBar(new MenuBarItem[]
        {
            new("_File", new MenuItem[]
            {
                new("_SQL Query", "Run SQL queries against Dataverse", () => NavigateToSqlQuery()),
                new("_Data Migration", "Import/export data", () => NavigateToDataMigration(), shortcut: Key.CtrlMask | Key.D),
                new("", "", () => {}, null, null, Key.Null), // Separator
                new("_Quit", "Exit the application", () => RequestStop(), shortcut: Key.CtrlMask | Key.Q)
            }),
            new("_Tools", new MenuItem[]
            {
                new("_Solutions", "Browse solutions", () => ShowNotImplemented("Solutions browser")),
                new("_Plugin Traces", "View plugin traces", () => ShowNotImplemented("Plugin traces")),
            }),
            new("_Help", new MenuItem[]
            {
                new("_About", "About PPDS", () => ShowAbout()),
                new("_Keyboard Shortcuts", "Show keyboard shortcuts", () => ShowKeyboardShortcuts()),
            })
        });

        Add(menu);
    }

    private void ShowMainMenu()
    {
        var container = new FrameView("Main Menu")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var label = new Label("Welcome to PPDS Interactive Mode\n\nSelect an option from the menu or use keyboard shortcuts:")
        {
            X = 2,
            Y = 1,
            Width = Dim.Fill() - 4,
            Height = 3
        };

        var buttonSql = new Button("SQL Query (F2)")
        {
            X = 2,
            Y = 5
        };
        buttonSql.Clicked += () => NavigateToSqlQuery();

        var buttonData = new Button("Data Migration (F3)")
        {
            X = 2,
            Y = 7
        };
        buttonData.Clicked += () => NavigateToDataMigration();

        var buttonQuit = new Button("Quit (Ctrl+Q)")
        {
            X = 2,
            Y = 10
        };
        buttonQuit.Clicked += () => RequestStop();

        // Profile info
        var profileLabel = new Label(_profileName != null
            ? $"Profile: {_profileName}"
            : "Profile: (none selected)")
        {
            X = Pos.Right(container) - 30,
            Y = Pos.Bottom(container) - 2,
            Width = 28,
            TextAlignment = TextAlignment.Right
        };

        container.Add(label, buttonSql, buttonData, buttonQuit, profileLabel);
        Add(container);

        // Global keyboard shortcuts
        KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.F2:
                    NavigateToSqlQuery();
                    e.Handled = true;
                    break;
                case Key.F3:
                    NavigateToDataMigration();
                    e.Handled = true;
                    break;
            }
        };
    }

    private void NavigateToSqlQuery()
    {
        var sqlScreen = new SqlQueryScreen(_profileName, _deviceCodeCallback, _session);
        Application.Run(sqlScreen);
    }

    private void NavigateToDataMigration()
    {
        ShowNotImplemented("Data Migration");
    }

    private void ShowNotImplemented(string feature)
    {
        MessageBox.Query("Not Implemented", $"{feature} is not yet implemented.\n\nThis feature is planned for a future release.", "OK");
    }

    private void ShowAbout()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "Unknown";
        MessageBox.Query("About PPDS",
            $"Power Platform Developer Suite\n\nVersion: {version}\n\nA multi-interface platform for Dataverse development.\n\nhttps://github.com/joshsmithxrm/ppds-sdk",
            "OK");
    }

    private void ShowKeyboardShortcuts()
    {
        MessageBox.Query("Keyboard Shortcuts",
            "Global Shortcuts:\n" +
            "  F2       - SQL Query\n" +
            "  F3       - Data Migration\n" +
            "  Ctrl+Q   - Quit\n\n" +
            "Table Navigation:\n" +
            "  Arrows   - Navigate cells\n" +
            "  PgUp/Dn  - Page up/down\n" +
            "  Home/End - First/last row\n" +
            "  /        - Filter results\n" +
            "  Ctrl+C   - Copy cell\n" +
            "  Ctrl+E   - Export\n",
            "OK");
    }
}
