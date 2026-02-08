using PPDS.Cli.Commands;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Branded splash screen shown during TUI startup.
/// Displays an animated hooded figure with glowing eyes (matching the PPDS banner),
/// flanking lightning bolts, PPDS logo, circuit traces, version, and initialization status.
/// </summary>
internal sealed class SplashView : View, ITuiStateCapture<SplashViewState>
{
    // Hooded figure ASCII art — 15 rows. Hood interior is mostly black void
    // with thin ░ fade at edges so the glowing eyes pop from pure darkness.
    // Color keys: 'H' = hood/cloak (Green), 'S' = shadow/void (DarkGray), 'E' = eyes (animated)
    private static readonly (string Text, char Color)[][] FigureRows =
    {
        // Hood peak — pointed tip
        new[] { ("▄▄", 'H') },                                                                        // 2w
        new[] { ("▄████▄", 'H') },                                                                    // 6w
        new[] { ("▄████████▄", 'H') },                                                                // 10w
        new[] { ("▄██████████████▄", 'H') },                                                          // 16w
        // Hood opening — ░ fade into black void
        new[] { ("██░", 'H'), ("              ", 'S'), ("░██", 'H') },                                 // 20w
        new[] { ("██░", 'H'), ("                  ", 'S'), ("░██", 'H') },                             // 24w
        new[] { ("██░", 'H'), ("                    ", 'S'), ("░██", 'H') },                           // 26w
        // *** EYES *** — bright slits floating in pure darkness
        new[] { ("██░", 'H'), ("    ", 'S'), ("▀▀▀", 'E'), ("      ", 'S'), ("▀▀▀", 'E'), ("    ", 'S'), ("░██", 'H') }, // 26w
        // Below eyes — void
        new[] { ("██░", 'H'), ("                    ", 'S'), ("░██", 'H') },                           // 26w
        // Hood narrows
        new[] { ("██░", 'H'), ("                  ", 'S'), ("░██", 'H') },                             // 24w
        new[] { ("██░", 'H'), ("              ", 'S'), ("░██", 'H') },                                 // 20w
        // Chin / neck
        new[] { ("██░", 'H'), ("          ", 'S'), ("░██", 'H') },                                    // 16w
        new[] { ("██████████████", 'H') },                                                              // 14w neck
        // Shoulders — cloak flares out
        new[] { ("▄██████████████████████▄", 'H') },                                                   // 24w
        new[] { ("█████", 'H'), ("░░", 'S'), ("██████████████", 'H'), ("░░", 'S'), ("█████", 'H') },  // 28w
    };

    // Lightning bolts — zigzag shapes, 8 rows each, max 6 chars wide.
    // Bolts align with figure rows 3–10 (the wide hood section).
    private static readonly (int Offset, string Text)[] LeftBolt =
    {
        (4, "██"),
        (3, "██"),
        (2, "██"),
        (0, "██████"),
        (2, "██"),
        (3, "██"),
        (0, "██████"),
        (2, "██"),
    };

    private static readonly (int Offset, string Text)[] RightBolt =
    {
        (0, "██"),
        (1, "██"),
        (2, "██"),
        (0, "██████"),
        (2, "██"),
        (1, "██"),
        (0, "██████"),
        (2, "██"),
    };

    // Bolt geometry
    private const int BoltStartRow = 3;
    private const int BoltDistFromCenter = 17;

    private static readonly string[] LogoLines =
    {
        " ██████  ██████  ██████  ███████",
        " ██   ██ ██   ██ ██   ██ ██     ",
        " ██████  ██████  ██   ██ ███████",
        " ██      ██      ██   ██      ██",
        " ██      ██      ██████  ███████",
    };

    private const string CircuitTrace = "── ● ──────────────────────────────── ● ──";
    private const string Tagline = "Power Platform Developer Suite";

    // Animation timing
    private const int RevealFrameMs = 50;
    private const int PulseFrameMs = 250;

    // Child views
    private readonly SplashArtView _artView;
    private readonly Label _versionLabel;
    private readonly TuiSpinner _spinner;
    private readonly Label _statusLabel;

    // Animation state
    private int _revealPhase;
    private object? _revealTimer;
    private object? _pulseTimer;

    // Logical state
    private readonly string _version;
    private string _statusMessage = "Initializing...";
    private bool _isReady;
    private bool _spinnerActive;

    public SplashView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = MakeSplashScheme(Color.Black, Color.Black);

        _version = ErrorOutput.Version;

        // Art view renders the multi-colored figure, lightning, logo, traces, tagline
        _artView = new SplashArtView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        // Version (below art area)
        _versionLabel = new Label($"v{_version}")
        {
            X = Pos.Center(),
            Y = Pos.Center() + 15,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = MakeSplashScheme(Color.DarkGray, Color.Black)
        };

        // Spinner
        _spinner = new TuiSpinner
        {
            X = Pos.Center() - 15,
            Y = Pos.Center() + 16,
            Width = 30,
            Height = 1
        };

        // Status label (shown when ready)
        _statusLabel = new Label(_statusMessage)
        {
            X = Pos.Center(),
            Y = Pos.Center() + 16,
            TextAlignment = TextAlignment.Centered
        };

        Add(_artView, _versionLabel, _spinner, _statusLabel);
        _spinnerActive = true;
    }

    /// <summary>
    /// Starts the reveal animation and spinner. Call after Application.Init().
    /// </summary>
    public void StartAnimation()
    {
        if (Application.Driver == null) return;

        _artView.RevealPhase = 0;
        _versionLabel.Visible = false;
        _statusLabel.Visible = false;
        _spinner.Visible = false;

        _revealPhase = 0;
        _revealTimer = Application.MainLoop?.AddTimeout(
            TimeSpan.FromMilliseconds(RevealFrameMs),
            OnRevealTick);

        _spinner.Start(_statusMessage);
        _spinner.Visible = false;
    }

    /// <summary>
    /// Starts the spinner animation. Calls StartAnimation for full reveal effect.
    /// </summary>
    public void StartSpinner() => StartAnimation();

    /// <summary>
    /// Updates the status message shown during initialization.
    /// </summary>
    public void SetStatus(string message)
    {
        if (_isReady) return;
        _statusMessage = message;
        if (Application.Driver != null)
        {
            _spinner.StopWithMessage(message);
            _spinner.Start(message);
        }
    }

    /// <summary>
    /// Marks initialization as complete. Stops the spinner and all animations.
    /// </summary>
    public void SetReady()
    {
        _isReady = true;
        _spinnerActive = false;
        _statusMessage = "Ready";

        // Stop reveal timer — pulse continues for eye/lightning animation
        if (_revealTimer != null)
        {
            Application.MainLoop?.RemoveTimeout(_revealTimer);
            _revealTimer = null;
        }

        // Ensure pulse animation is running (may not have started if init beat the reveal)
        if (_pulseTimer == null)
            StartPulse();

        if (Application.Driver != null)
        {
            _artView.RevealPhase = 100;
            _artView.SetNeedsDisplay();
            _versionLabel.Visible = true;

            _spinner.Stop();
            _spinner.Visible = false;
            _statusLabel.Text = "Ctrl+T — New Query   Alt+T — Tools Menu";
            _statusLabel.Visible = true;
        }
    }

    /// <inheritdoc />
    public SplashViewState CaptureState() => new(
        StatusMessage: _statusMessage,
        IsReady: _isReady,
        Version: _version,
        SpinnerActive: _spinnerActive && !_isReady);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) StopAllTimers();
        base.Dispose(disposing);
    }

    // Reveal frame schedule (50ms per frame, ~950ms total):
    //  1:      top circuit trace
    //  2-16:   figure rows 0-14 (hood + body materializes top-down)
    //          lightning bolts appear alongside figure rows 3-10
    //  17:     PPDS logo + tagline
    //  18:     bottom trace + version + spinner → start pulse
    private bool OnRevealTick(MainLoop mainLoop)
    {
        _revealPhase++;
        _artView.RevealPhase = _revealPhase;
        _artView.SetNeedsDisplay();

        if (_revealPhase >= 18)
        {
            _versionLabel.Visible = true;
            _spinner.Visible = true;
            _revealTimer = null;
            StartPulse();
            return false;
        }

        return true;
    }

    private void StartPulse()
    {
        _pulseTimer = Application.MainLoop?.AddTimeout(
            TimeSpan.FromMilliseconds(PulseFrameMs),
            OnPulseTick);
    }

    private bool OnPulseTick(MainLoop mainLoop)
    {
        _artView.PulseFrame++;
        _artView.SetNeedsDisplay();
        return true;
    }

    private void StopAllTimers()
    {
        if (_revealTimer != null)
        {
            Application.MainLoop?.RemoveTimeout(_revealTimer);
            _revealTimer = null;
        }
        if (_pulseTimer != null)
        {
            Application.MainLoop?.RemoveTimeout(_pulseTimer);
            _pulseTimer = null;
        }
    }

    private static ColorScheme MakeSplashScheme(Color fg, Color bg) => new()
    {
        Normal = MakeSplashAttr(fg, bg),
        Focus = MakeSplashAttr(fg, bg),
        HotNormal = MakeSplashAttr(fg, bg),
        HotFocus = MakeSplashAttr(fg, bg),
        Disabled = MakeSplashAttr(fg, bg)
    };

    private static Terminal.Gui.Attribute MakeSplashAttr(Color fg, Color bg) =>
        Application.Driver == null
            ? new Terminal.Gui.Attribute(fg, bg)
            : Application.Driver.MakeAttribute(fg, bg);

    /// <summary>
    /// Custom view that renders the multi-colored hooded figure, lightning bolts,
    /// PPDS logo, circuit traces, and tagline with per-character color control.
    /// </summary>
    private sealed class SplashArtView : View
    {
        internal int RevealPhase;
        internal int PulseFrame;

        public override void Redraw(Rect bounds)
        {
            if (Driver == null) { Clear(); return; }

            // Force black background before clearing — prevents inherited Focus scheme leaking through
            Application.Driver.SetAttribute(Application.Driver.MakeAttribute(Color.Black, Color.Black));
            Clear();

            var centerX = bounds.Width / 2;
            var centerY = bounds.Height / 2;

            // Animation states derived from PulseFrame
            var eyeFrame = PulseFrame % 8;
            var boltVisible = PulseFrame % 10 < 3;
            var traceBright = (PulseFrame / 2) % 2 == 0;

            // Colors — multi-color scheme
            var hoodColor = Application.Driver.MakeAttribute(Color.Green, Color.Black);
            var shadowColor = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black);

            // Eyes cycle: mostly BrightGreen with brief flashes of White and BrightCyan
            Color eyeFg = eyeFrame switch
            {
                3 => Color.White,
                7 => Color.BrightCyan,
                _ => Color.BrightGreen
            };
            var eyeColor = Application.Driver.MakeAttribute(eyeFg, Color.Black);

            // Lightning: bright yellow flashes
            var boltColor = Application.Driver.MakeAttribute(Color.BrightYellow, Color.Black);

            // Traces pulse between green and cyan
            var traceColor = traceBright
                ? Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
                : Application.Driver.MakeAttribute(Color.Green, Color.Black);

            var logoColor = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black);
            var taglineColor = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black);

            var y = centerY - 11;

            // Phase 1: Top circuit trace
            if (RevealPhase >= 1)
                DrawCentered(bounds, centerX, y, CircuitTrace, traceColor);
            y++;

            // Phase 2-16: Figure rows (hood materializes top-down)
            for (var i = 0; i < FigureRows.Length; i++)
            {
                if (RevealPhase >= 2 + i)
                {
                    DrawFigureRow(bounds, centerX, y, FigureRows[i], hoodColor, shadowColor, eyeColor);

                    // Draw lightning bolts alongside figure rows 3-10
                    var boltRow = i - BoltStartRow;
                    if (boltRow >= 0 && boltRow < LeftBolt.Length && boltVisible)
                    {
                        DrawBoltRow(bounds, centerX - BoltDistFromCenter - 6, y, LeftBolt[boltRow], boltColor);
                        DrawBoltRow(bounds, centerX + BoltDistFromCenter, y, RightBolt[boltRow], boltColor);
                    }
                }
                y++;
            }
            y++; // blank after figure

            // Phase 17: PPDS logo + tagline
            if (RevealPhase >= 17)
            {
                foreach (var line in LogoLines)
                {
                    DrawCentered(bounds, centerX, y, line, logoColor);
                    y++;
                }
                y++; // blank
                DrawCentered(bounds, centerX, y, Tagline, taglineColor);
                y++;
            }
            else
            {
                y += LogoLines.Length + 2; // skip
            }

            // Phase 18: Bottom circuit trace
            if (RevealPhase >= 18)
                DrawCentered(bounds, centerX, y, CircuitTrace, traceColor);
        }

        private static void DrawCentered(Rect bounds, int centerX, int y, string text, Terminal.Gui.Attribute color)
        {
            if (y < 0 || y >= bounds.Height) return;
            var x = centerX - text.Length / 2;
            Application.Driver.SetAttribute(color);
            Application.Driver.Move(x, y);
            Application.Driver.AddStr(text);
        }

        private static void DrawFigureRow(
            Rect bounds, int centerX, int y,
            (string Text, char Color)[] segments,
            Terminal.Gui.Attribute hoodColor, Terminal.Gui.Attribute shadowColor, Terminal.Gui.Attribute eyeColor)
        {
            if (y < 0 || y >= bounds.Height) return;
            var totalLen = 0;
            foreach (var seg in segments) totalLen += seg.Text.Length;
            var x = centerX - totalLen / 2;

            foreach (var (text, colorKey) in segments)
            {
                var color = colorKey switch
                {
                    'E' => eyeColor,
                    'S' => shadowColor,
                    _ => hoodColor
                };
                Application.Driver.SetAttribute(color);
                Application.Driver.Move(x, y);
                Application.Driver.AddStr(text);
                x += text.Length;
            }
        }

        private static void DrawBoltRow(
            Rect bounds, int startX, int y,
            (int Offset, string Text) bolt, Terminal.Gui.Attribute color)
        {
            if (y < 0 || y >= bounds.Height) return;
            var x = startX + bolt.Offset;
            if (x < 0 || x + bolt.Text.Length > bounds.Width) return;
            Application.Driver.SetAttribute(color);
            Application.Driver.Move(x, y);
            Application.Driver.AddStr(bolt.Text);
        }
    }
}
