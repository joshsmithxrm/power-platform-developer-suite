using System.Reflection;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Installs <see cref="PpdsClipboard"/> into Terminal.Gui's driver via reflection.
/// ConsoleDriver.Clipboard has no public setter, so we set the private backing field.
/// This must be called after <see cref="Application.Init()"/>.
/// </summary>
internal static class PpdsClipboardInstaller
{
    public static void Install()
    {
        var driver = Application.Driver;
        if (driver == null) return;

        var ppdsClipboard = new PpdsClipboard();

        // CursesDriver has field "clipboard", NetDriver has auto-property backing field.
        // Try both patterns.
        var driverType = driver.GetType();
        var field = driverType.GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? driverType.GetField("<Clipboard>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field != null)
        {
            field.SetValue(driver, ppdsClipboard);
            TuiDebugLog.Log($"Clipboard: installed PpdsClipboard on {driverType.Name}");
        }
        else
        {
            TuiDebugLog.Log($"Clipboard: could not find clipboard field on {driverType.Name} — using Terminal.Gui default");
        }
    }
}
