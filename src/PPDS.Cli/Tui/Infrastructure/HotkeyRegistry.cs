using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// The context layer for a hotkey registration.
/// </summary>
public enum HotkeyScope
{
    /// <summary>Global hotkeys work everywhere, even closing dialogs first.</summary>
    Global,

    /// <summary>Screen hotkeys only work when that screen is active (no dialog open).</summary>
    Screen,

    /// <summary>Dialog hotkeys only work when that specific dialog is open.</summary>
    Dialog
}

/// <summary>
/// A registered hotkey with its handler and metadata.
/// </summary>
public sealed class HotkeyBinding
{
    /// <summary>The key combination.</summary>
    public Key Key { get; init; }

    /// <summary>The scope this binding applies to.</summary>
    public HotkeyScope Scope { get; init; }

    /// <summary>Human-readable description for help display.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Action to invoke when the hotkey is pressed.</summary>
    public Action Handler { get; init; } = null!;

    /// <summary>Owner object (screen or dialog) for scope filtering.</summary>
    public object? Owner { get; init; }
}

/// <summary>
/// Service for registering and routing keyboard shortcuts across the TUI.
/// Provides context-aware hotkey management with three scope layers:
/// Global (works everywhere), Screen (active screen only), Dialog (active dialog only).
/// </summary>
public interface IHotkeyRegistry
{
    /// <summary>
    /// Registers a hotkey. Throws if key already registered in same scope.
    /// </summary>
    /// <param name="key">The key combination (e.g., Key.CtrlMask | Key.E).</param>
    /// <param name="scope">Global, Screen, or Dialog scope.</param>
    /// <param name="description">Human-readable description for help.</param>
    /// <param name="handler">Action to invoke when key is pressed.</param>
    /// <param name="owner">Owner object (screen/dialog) for scope filtering.</param>
    /// <returns>Registration token - dispose to unregister.</returns>
    IDisposable Register(Key key, HotkeyScope scope, string description, Action handler, object? owner = null);

    /// <summary>
    /// Attempts to handle a key event. Returns true if handled.
    /// Priority: Dialog > Screen > Global.
    /// Global hotkeys close any open dialog before executing.
    /// </summary>
    bool TryHandle(KeyEvent keyEvent);

    /// <summary>
    /// Gets all registered hotkeys for help display.
    /// </summary>
    IReadOnlyList<HotkeyBinding> GetAllBindings();

    /// <summary>
    /// Gets bindings relevant to the current context (global + active screen/dialog).
    /// </summary>
    IReadOnlyList<HotkeyBinding> GetContextBindings();

    /// <summary>
    /// Sets the current active screen for screen-scope filtering.
    /// </summary>
    void SetActiveScreen(object? screen);

    /// <summary>
    /// Gets the current active screen.
    /// </summary>
    object? ActiveScreen { get; }

    /// <summary>
    /// Sets the current open dialog for dialog-scope filtering.
    /// </summary>
    void SetActiveDialog(object? dialog);

    /// <summary>
    /// Suppresses the next bare Alt key from focusing the menu bar.
    /// Call this after handling Alt+key combinations to prevent menu focus on Alt release.
    /// </summary>
    void SuppressNextAltMenuFocus();

    /// <summary>
    /// Sets the menu bar reference for Alt key suppression.
    /// </summary>
    void SetMenuBar(MenuBar? menuBar);
}

/// <summary>
/// Central registry for context-aware keyboard shortcuts.
/// </summary>
internal sealed class HotkeyRegistry : IHotkeyRegistry
{
    private readonly List<HotkeyBinding> _bindings = new();
    private readonly object _lock = new();
    private bool _globalHandlerExecuting;
    private bool _suppressAltMenuFocus;
    private MenuBar? _menuBar;
    private System.Reflection.FieldInfo? _openedByAltKeyField;

    private object? _activeScreen;
    private object? _activeDialog;

    public IDisposable Register(Key key, HotkeyScope scope, string description, Action handler, object? owner = null)
    {
        lock (_lock)
        {
            // Check for conflicts: same key in same scope with same owner (or any owner for Global)
            var conflict = _bindings.FirstOrDefault(b =>
                b.Key == key &&
                b.Scope == scope &&
                (scope == HotkeyScope.Global || b.Owner == owner));

            if (conflict != null)
            {
                throw new InvalidOperationException(
                    $"Hotkey {FormatKey(key)} already registered for '{conflict.Description}' in {scope} scope");
            }

            var binding = new HotkeyBinding
            {
                Key = key,
                Scope = scope,
                Description = description,
                Handler = handler,
                Owner = owner
            };

            _bindings.Add(binding);
            TuiDebugLog.Log($"Hotkey registered: {FormatKey(key)} -> {description} [{scope}]");

            return new RegistrationToken(this, binding);
        }
    }

    public void SetMenuBar(MenuBar? menuBar)
    {
        _menuBar = menuBar;
        if (menuBar != null)
        {
            // Cache the reflection field for performance
            _openedByAltKeyField = typeof(MenuBar).GetField(
                "openedByAltKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        }
    }

    public void SuppressNextAltMenuFocus()
    {
        _suppressAltMenuFocus = true;

        // Reset the MenuBar's internal state using reflection
        // This prevents OnKeyUp from opening the menu when Alt is released
        if (_menuBar != null && _openedByAltKeyField != null)
        {
            _openedByAltKeyField.SetValue(_menuBar, false);
        }
    }

    public bool TryHandle(KeyEvent keyEvent)
    {
        // Suppress bare Alt key to prevent menu focus after Alt+modifier combinations
        if (keyEvent.Key == Key.AltMask && _suppressAltMenuFocus)
        {
            _suppressAltMenuFocus = false;
            return true;
        }

        // Block letter keys when menu dropdown is open to prevent first-letter navigation
        // This prevents accidental menu item selection (e.g., Q selecting Quit when File menu is open)
        // Block both plain letters and Alt+letter combinations
        if (_menuBar != null && _menuBar.IsMenuOpen)
        {
            var key = keyEvent.Key;
            var baseKey = key & ~Key.AltMask & ~Key.CtrlMask & ~Key.ShiftMask;
            var keyValue = (int)baseKey;

            bool isLetter = (keyValue >= 'a' && keyValue <= 'z') ||
                            (keyValue >= 'A' && keyValue <= 'Z');

            // Block plain letters and Alt+letter (but not Ctrl+letter which might be shortcuts)
            bool hasCtrl = (key & Key.CtrlMask) != 0;
            if (isLetter && !hasCtrl)
            {
                TuiDebugLog.Log($"Blocking letter key '{(char)keyValue}' (Alt={(key & Key.AltMask) != 0}) while menu is open");
                return true; // Consume the key - prevents first-letter navigation
            }
        }

        HotkeyBinding? matchedBinding = null;
        bool isGlobalWithDialogOpen = false;

        lock (_lock)
        {
            // Priority order: Dialog > Screen > Global

            // 1. Check dialog-scope bindings first (if dialog is open)
            if (_activeDialog != null)
            {
                matchedBinding = _bindings.FirstOrDefault(b =>
                    b.Key == keyEvent.Key &&
                    b.Scope == HotkeyScope.Dialog &&
                    b.Owner == _activeDialog);
            }

            // 2. Check screen-scope bindings (only if no dialog is open)
            if (matchedBinding == null && _activeDialog == null && _activeScreen != null)
            {
                matchedBinding = _bindings.FirstOrDefault(b =>
                    b.Key == keyEvent.Key &&
                    b.Scope == HotkeyScope.Screen &&
                    b.Owner == _activeScreen);
            }

            // 3. Check global bindings (always available)
            if (matchedBinding == null)
            {
                matchedBinding = _bindings.FirstOrDefault(b =>
                    b.Key == keyEvent.Key &&
                    b.Scope == HotkeyScope.Global);

                if (matchedBinding != null && _activeDialog != null)
                {
                    isGlobalWithDialogOpen = true;
                }
            }
        }

        if (matchedBinding == null)
            return false;

        TuiDebugLog.Log($"Hotkey matched: {FormatKey(keyEvent.Key)} -> {matchedBinding.Description}");

        // CRITICAL: Global hotkeys need special handling to avoid Terminal.Gui state corruption.
        // - Only one global handler can be pending/executing at a time (any key)
        // - If dialog is open, just close it - don't execute the new handler
        // - User must press key again after dialog closes to trigger its action
        // This prevents overlapping Application.Run() calls that corrupt ConsoleDriver state.
        if (matchedBinding.Scope == HotkeyScope.Global)
        {
            lock (_lock)
            {
                if (_globalHandlerExecuting)
                {
                    TuiDebugLog.Log($"Ignoring {FormatKey(keyEvent.Key)} - global handler already active");
                    return true; // Absorbed, not executed
                }
                _globalHandlerExecuting = true;
            }

            // If dialog is open, ONLY close it - don't execute the new handler.
            // This avoids the race condition where RequestStop() hasn't finished
            // before a new Application.Run() starts.
            if (isGlobalWithDialogOpen)
            {
                TuiDebugLog.Log("Closing dialog (handler will not execute - press again after dialog closes)");
                SetActiveDialog(null);
                Application.RequestStop();
                // Reset gate immediately - dialog will close on its own
                lock (_lock) { _globalHandlerExecuting = false; }
                return true;
            }

            // Defer handler execution to next main loop iteration.
            // Starting Application.Run() from within a key event handler corrupts
            // Terminal.Gui's internal state (Border.SetBorderBrush null reference).
            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    matchedBinding.Handler();
                }
                finally
                {
                    lock (_lock) { _globalHandlerExecuting = false; }
                }
            });
        }
        else
        {
            // Screen/Dialog scope hotkeys are expected to run in their context
            // and typically don't start new Application.Run loops
            matchedBinding.Handler();
        }

        return true;
    }

    public void SetActiveScreen(object? screen)
    {
        _activeScreen = screen;
        TuiDebugLog.Log($"Active screen: {screen?.GetType().Name ?? "null"}");
    }

    public object? ActiveScreen => _activeScreen;

    public void SetActiveDialog(object? dialog)
    {
        _activeDialog = dialog;
        TuiDebugLog.Log($"Active dialog: {dialog?.GetType().Name ?? "null"}");
    }

    public IReadOnlyList<HotkeyBinding> GetAllBindings()
    {
        lock (_lock)
        {
            return _bindings.ToList().AsReadOnly();
        }
    }

    public IReadOnlyList<HotkeyBinding> GetContextBindings()
    {
        lock (_lock)
        {
            var result = new List<HotkeyBinding>();

            // Always include global bindings
            result.AddRange(_bindings.Where(b => b.Scope == HotkeyScope.Global));

            // Include active screen bindings (if no dialog open)
            if (_activeDialog == null && _activeScreen != null)
            {
                result.AddRange(_bindings.Where(b =>
                    b.Scope == HotkeyScope.Screen &&
                    b.Owner == _activeScreen));
            }

            // Include active dialog bindings
            if (_activeDialog != null)
            {
                result.AddRange(_bindings.Where(b =>
                    b.Scope == HotkeyScope.Dialog &&
                    b.Owner == _activeDialog));
            }

            return result.AsReadOnly();
        }
    }

    internal void Unregister(HotkeyBinding binding)
    {
        lock (_lock)
        {
            _bindings.Remove(binding);
            TuiDebugLog.Log($"Hotkey unregistered: {FormatKey(binding.Key)} -> {binding.Description}");
        }
    }

    /// <summary>
    /// Formats a key combination for display.
    /// </summary>
    public static string FormatKey(Key key)
    {
        var parts = new List<string>();
        if ((key & Key.CtrlMask) != 0) parts.Add("Ctrl");
        if ((key & Key.AltMask) != 0) parts.Add("Alt");
        if ((key & Key.ShiftMask) != 0) parts.Add("Shift");

        var baseKey = key & ~Key.CtrlMask & ~Key.AltMask & ~Key.ShiftMask;

        // Handle special cases for cleaner display
        var keyName = baseKey switch
        {
            Key.DeleteChar => "Delete",
            Key.Backspace => "Backspace",
            Key.Enter => "Enter",
            Key.Esc => "Esc",
            Key.Tab => "Tab",
            Key.Space => "Space",
            _ => baseKey.ToString()
        };

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private sealed class RegistrationToken : IDisposable
    {
        private readonly HotkeyRegistry _registry;
        private readonly HotkeyBinding _binding;
        private bool _disposed;

        public RegistrationToken(HotkeyRegistry registry, HotkeyBinding binding)
        {
            _registry = registry;
            _binding = binding;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _registry.Unregister(_binding);
        }
    }
}
