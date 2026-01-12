using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog displaying product information, version, and links.
/// </summary>
internal sealed class AboutDialog : Dialog
{
    private const string ProductName = "Power Platform Developer Suite";
    private const string Tagline = "Pro-grade tooling for Power Platform developers";
    private const string DocsUrl = "https://joshsmithxrm.github.io/ppds-docs/";
    private const string GitHubUrl = "https://github.com/joshsmithxrm/power-platform-developer-suite";
    private const string Copyright = "(c) 2025-2026 joshsmithxrm";

    /// <summary>
    /// Creates a new About dialog.
    /// </summary>
    public AboutDialog() : base("About PPDS")
    {
        Width = 72;
        Height = 16;
        ColorScheme = TuiColorPalette.Default;

        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString() ?? "Unknown";

        // Product name (centered header)
        var productLabel = new Label(ProductName)
        {
            X = Pos.Center(),
            Y = 1
        };

        // Version
        var versionLabel = new Label($"Version: {version}")
        {
            X = Pos.Center(),
            Y = 3
        };

        // Tagline
        var taglineLabel = new Label(Tagline)
        {
            X = Pos.Center(),
            Y = 5
        };

        // Separator
        var separator1 = new Label(new string('─', 68))
        {
            X = 1,
            Y = 7
        };

        // Links section
        const int labelWidth = 15;
        const int valueX = 17;

        var docsHeaderLabel = new Label("Documentation:")
        {
            X = 1,
            Y = 9,
            Width = labelWidth
        };
        var docsUrlLabel = new Label(DocsUrl)
        {
            X = valueX,
            Y = 9
        };

        var githubHeaderLabel = new Label("GitHub:")
        {
            X = 1,
            Y = 10,
            Width = labelWidth
        };
        var githubUrlLabel = new Label(GitHubUrl)
        {
            X = valueX,
            Y = 10
        };

        // Separator
        var separator2 = new Label(new string('─', 68))
        {
            X = 1,
            Y = 12
        };

        // Copyright
        var copyrightLabel = new Label(Copyright)
        {
            X = Pos.Center(),
            Y = 13
        };

        // Close button
        var closeButton = new Button("_Close")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(
            productLabel,
            versionLabel,
            taglineLabel,
            separator1,
            docsHeaderLabel, docsUrlLabel,
            githubHeaderLabel, githubUrlLabel,
            separator2,
            copyrightLabel,
            closeButton
        );

        // Handle Escape to close
        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                Application.RequestStop();
                e.Handled = true;
            }
        };
    }
}
