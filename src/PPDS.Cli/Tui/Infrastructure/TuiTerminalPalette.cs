using System.Text;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Manages terminal palette override via OSC 4 escape sequences.
/// Forces the terminal's 16 ANSI colors to specific RGB values for consistent
/// appearance across terminal emulators. Restores defaults on exit via OSC 104.
/// </summary>
/// <remarks>
/// Terminal.Gui v1 only supports 16 ANSI colors via the <c>Color</c> enum.
/// Different terminals remap these differently (e.g., Windows Terminal "One Half Dark"
/// maps Black to #282C34 instead of #000000). By emitting OSC 4 sequences on startup,
/// we force a consistent dark palette regardless of the user's terminal theme.
/// Respects NO_COLOR (https://no-color.org/) and skips when stdout is redirected.
/// </remarks>
internal static class TuiTerminalPalette
{
    // ANSI color index → RGB hex components.
    // Terminal.Gui Color enum mapping:
    //   Black=0, Red=1, Green=2, Brown/Yellow=3, Blue=4, Magenta=5, Cyan=6, Gray=7
    //   DarkGray=8, BrightRed=9, BrightGreen=10, BrightYellow=11,
    //   BrightBlue=12, BrightMagenta=13, BrightCyan=14, White=15
    private static readonly (int Index, string R, string G, string B)[] Palette =
    {
        (0,  "00", "00", "00"),  // Black — pure black background
        (1,  "cc", "00", "00"),  // Red — production status bar
        (2,  "00", "cc", "00"),  // Green — development status bar
        (3,  "cc", "88", "00"),  // Brown/Yellow — sandbox status bar
        (4,  "00", "44", "cc"),  // Blue
        (5,  "cc", "00", "cc"),  // Magenta
        (6,  "00", "cc", "cc"),  // Cyan — primary accent
        (7,  "aa", "aa", "aa"),  // Gray — muted text, StatusBar_Default
        (8,  "55", "55", "55"),  // DarkGray — disabled text, TabActive bg
        (9,  "ff", "33", "33"),  // BrightRed — error focus
        (10, "33", "ff", "33"),  // BrightGreen — success focus
        (11, "ff", "ff", "33"),  // BrightYellow — sandbox focus
        (12, "33", "77", "ff"),  // BrightBlue
        (13, "ff", "33", "ff"),  // BrightMagenta
        (14, "33", "ff", "ff"),  // BrightCyan — accent highlight
        (15, "ff", "ff", "ff"),  // White — primary text
    };

    /// <summary>
    /// Emits OSC 4 sequences to override the terminal's 16-color palette.
    /// No-op when NO_COLOR is set or stdout is redirected.
    /// </summary>
    public static void Apply()
    {
        if (!ShouldEmit()) return;

        try
        {
            Console.Out.Write(BuildApplySequence());
            Console.Out.Flush();
        }
        catch
        {
            // Terminal escape sequences are best-effort — unsupported terminals ignore them
        }
    }

    /// <summary>
    /// Emits OSC 104 to restore the terminal's default palette.
    /// No-op when NO_COLOR is set or stdout is redirected.
    /// </summary>
    public static void Restore()
    {
        if (!ShouldEmit()) return;

        try
        {
            Console.Out.Write(BuildRestoreSequence());
            Console.Out.Flush();
        }
        catch
        {
            // Best-effort restore
        }
    }

    /// <summary>
    /// Determines whether palette escape sequences should be emitted.
    /// Returns false when NO_COLOR is set or stdout is redirected.
    /// </summary>
    internal static bool ShouldEmit()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;

        if (Console.IsOutputRedirected)
            return false;

        return true;
    }

    /// <summary>
    /// Builds the OSC 4 escape string for all 16 colors. Used by <see cref="Apply"/>
    /// and exposed internally for unit testing without writing to console.
    /// </summary>
    internal static string BuildApplySequence()
    {
        var sb = new StringBuilder();
        foreach (var (index, r, g, b) in Palette)
        {
            // OSC 4 format: ESC ] 4 ; <index> ; rgb:<rr>/<gg>/<bb> BEL
            sb.Append($"\x1b]4;{index};rgb:{r}/{g}/{b}\x07");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the OSC 104 restore escape string. Used by <see cref="Restore"/>
    /// and exposed internally for unit testing.
    /// </summary>
    internal static string BuildRestoreSequence() => "\x1b]104\x07";

    /// <summary>
    /// Returns the palette definitions for unit testing validation.
    /// </summary>
    internal static IReadOnlyList<(int Index, string R, string G, string B)> GetPaletteDefinitions()
        => Palette;
}
