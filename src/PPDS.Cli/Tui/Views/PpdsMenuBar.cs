using System.Reflection;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Custom MenuBar subclass that fixes two flicker issues in Terminal.Gui v1.19:
///
/// 1. Alt key toggle: Terminal.Gui uses bare Alt press+release to toggle menus open/closed.
///    On Windows, the console driver generates extra Alt events (focus changes, mouse near title bar),
///    making the behavior unpredictable. This subclass makes Alt only OPEN menus, never close them.
///    Users close menus with Esc, clicking a menu item, or clicking outside.
///
/// 2. Mouse click flicker: The Windows console driver can generate both Button1Pressed and
///    Button1Clicked for a single physical click. MenuBar closes on Pressed then opens on Clicked,
///    causing visible flicker. A timestamp-based debounce prevents this.
///
/// All menu-related reflection and workarounds are consolidated here.
/// </summary>
internal sealed class PpdsMenuBar : MenuBar
{
    private static readonly FieldInfo? OpenedByHotKeyField = typeof(MenuBar).GetField(
        "openedByHotKey",
        BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? CloseAllMenusMethod = typeof(MenuBar).GetMethod(
        "CloseAllMenus",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private long _lastMenuOpenTicks;

    public PpdsMenuBar(MenuBarItem[] menus) : base(menus)
    {
    }

    /// <summary>
    /// Prevents bare Alt release from closing an open menu.
    /// Alt can open menus but not close them — matches VS Code/Windows Terminal behavior.
    /// </summary>
    public override bool OnKeyUp(KeyEvent keyEvent)
    {
        // Bare Alt release (no character key)
        if (keyEvent.Key == Key.AltMask)
        {
            if (IsMenuOpen)
            {
                TuiDebugLog.Log("Suppressing Alt close — menu stays open");
                return true; // Consume event, don't call base, menu stays open
            }

            // Menu is closed — let base handle it (normal open behavior)
            TuiDebugLog.Log("Allowing Alt open — menu is closed");
        }

        return base.OnKeyUp(keyEvent);
    }

    /// <summary>
    /// Preserves openedByHotKey state when Alt is pressed while menu is open.
    /// Terminal.Gui's OnKeyDown unconditionally clears openedByHotKey on Alt press,
    /// which breaks protection against Alt-release closing hotkey-opened menus.
    /// </summary>
    public override bool OnKeyDown(KeyEvent keyEvent)
    {
        if (keyEvent.Key == Key.AltMask && IsMenuOpen && OpenedByHotKeyField != null)
        {
            var wasOpenedByHotKey = (bool)(OpenedByHotKeyField.GetValue(this) ?? false);
            var result = base.OnKeyDown(keyEvent);

            if (wasOpenedByHotKey)
            {
                OpenedByHotKeyField.SetValue(this, true);
                TuiDebugLog.Log("Restored openedByHotKey after Alt KeyDown");
            }

            return result;
        }

        return base.OnKeyDown(keyEvent);
    }

    /// <summary>
    /// Debounces rapid mouse events that cause flicker on Windows console driver.
    /// The Windows console can generate both Button1Pressed and Button1Clicked for a single
    /// physical click. MenuBar closes on Pressed then opens on Clicked, causing visible flicker.
    /// When the menu is open and a close-triggering mouse event arrives within 300ms of opening,
    /// we suppress it by not passing the event to the base class.
    /// </summary>
    public override bool MouseEvent(MouseEvent me)
    {
        // If menu is open and we're within the debounce window, suppress close-triggering events
        // by not calling base. This must happen BEFORE base.MouseEvent() which would close the menu.
        if (IsMenuOpen)
        {
            var elapsed = Environment.TickCount64 - _lastMenuOpenTicks;
            if (elapsed < 300 && (me.Flags == MouseFlags.Button1Pressed ||
                                  me.Flags == MouseFlags.Button1Clicked))
            {
                TuiDebugLog.Log($"Debouncing mouse event ({me.Flags}) — {elapsed}ms since open");
                return true; // Consume event, don't let base close the menu
            }
        }

        var wasOpen = IsMenuOpen;
        var result = base.MouseEvent(me);

        // Track when menu opens for future debounce checks
        if (!wasOpen && IsMenuOpen)
        {
            _lastMenuOpenTicks = Environment.TickCount64;
            TuiDebugLog.Log("Menu opened via mouse");
        }
        else if (wasOpen && !IsMenuOpen)
        {
            TuiDebugLog.Log("Menu closed via mouse");
        }

        return result;
    }

    /// <summary>
    /// Programmatically closes the menu. Used when quitting to prevent state corruption.
    /// </summary>
    public void CloseMenu()
    {
        if (!IsMenuOpen) return;

        try
        {
            CloseAllMenusMethod?.Invoke(this, null);
            TuiDebugLog.Log("Menu closed programmatically via CloseMenu()");
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Failed to close menu: {ex.Message}");
        }
    }
}
