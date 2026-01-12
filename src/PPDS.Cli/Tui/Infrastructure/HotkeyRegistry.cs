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
    /// Sets the current open dialog for dialog-scope filtering.
    /// </summary>
    void SetActiveDialog(object? dialog);
}

/// <summary>
/// Central registry for context-aware keyboard shortcuts.
/// </summary>
internal sealed class HotkeyRegistry : IHotkeyRegistry
{
    private readonly List<HotkeyBinding> _bindings = new();
    private readonly object _lock = new();

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

    public bool TryHandle(KeyEvent keyEvent)
    {
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

        // For global hotkeys, close dialog first if one is open
        if (isGlobalWithDialogOpen)
        {
            TuiDebugLog.Log("Closing dialog before executing global hotkey");
            SetActiveDialog(null);  // Clear dialog state immediately to prevent stale state
            Application.RequestStop();
            // Use MainLoop.Invoke to execute handler after dialog closes
            Application.MainLoop?.Invoke(() => matchedBinding.Handler());
        }
        else
        {
            matchedBinding.Handler();
        }

        return true;
    }

    public void SetActiveScreen(object? screen)
    {
        _activeScreen = screen;
        TuiDebugLog.Log($"Active screen: {screen?.GetType().Name ?? "null"}");
    }

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
