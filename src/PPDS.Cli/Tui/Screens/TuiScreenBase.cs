using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Abstract base class for TUI screens, providing common lifecycle management.
/// All new screens should inherit from this class.
/// </summary>
/// <remarks>
/// Provides:
/// <list type="bullet">
/// <item>Content View setup with Dim.Fill()</item>
/// <item>Session and ErrorService references</item>
/// <item>Hotkey registration with automatic cleanup</item>
/// <item>Per-screen CancellationToken (fires on Dispose)</item>
/// <item>Environment URL binding</item>
/// <item>Standard Dispose pattern</item>
/// </list>
/// </remarks>
internal abstract class TuiScreenBase : ITuiScreen
{
    protected readonly InteractiveSession Session;
    protected readonly ITuiErrorService ErrorService;
    private readonly List<IDisposable> _hotkeyRegistrations = new();
    private readonly CancellationTokenSource _screenCts = new();
    private bool _disposed;

    /// <inheritdoc />
    public View Content { get; }

    /// <inheritdoc />
    public abstract string Title { get; }

    /// <inheritdoc />
    public virtual MenuBarItem[]? ScreenMenuItems => null;

    /// <inheritdoc />
    public virtual Action? ExportAction => null;

    /// <inheritdoc />
    public event Action? CloseRequested;

    /// <inheritdoc />
    public event Action? MenuStateChanged;

    /// <summary>
    /// The environment URL this screen is bound to.
    /// Screens can operate independently on different environments.
    /// </summary>
    public string? EnvironmentUrl { get; protected set; }

    /// <summary>
    /// Cancellation token that fires when the screen is closed or disposed.
    /// Use this instead of CancellationToken.None for all async operations.
    /// </summary>
    protected CancellationToken ScreenCancellation => _screenCts.Token;

    protected TuiScreenBase(InteractiveSession session, string? environmentUrl = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ErrorService = session.GetErrorService();
        EnvironmentUrl = environmentUrl ?? session.CurrentEnvironmentUrl;

        Content = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = TuiColorPalette.Default
        };
    }

    /// <inheritdoc />
    public void OnActivated(IHotkeyRegistry hotkeyRegistry)
    {
        RegisterHotkeys(hotkeyRegistry);
    }

    /// <inheritdoc />
    public void OnDeactivating()
    {
        foreach (var reg in _hotkeyRegistrations)
        {
            reg.Dispose();
        }
        _hotkeyRegistrations.Clear();
    }

    /// <summary>
    /// Override to register screen-scope hotkeys. Called during OnActivated.
    /// Use <see cref="RegisterHotkey"/> to auto-track registrations for cleanup.
    /// </summary>
    protected abstract void RegisterHotkeys(IHotkeyRegistry registry);

    /// <summary>
    /// Registers a screen-scope hotkey and tracks the registration for automatic cleanup.
    /// </summary>
    protected void RegisterHotkey(IHotkeyRegistry registry, Key key, string description, Action handler)
    {
        _hotkeyRegistrations.Add(
            registry.Register(key, HotkeyScope.Screen, description, handler, owner: this));
    }

    /// <summary>
    /// Raises the <see cref="CloseRequested"/> event.
    /// </summary>
    protected void RequestClose() => CloseRequested?.Invoke();

    /// <summary>
    /// Raises the <see cref="MenuStateChanged"/> event.
    /// </summary>
    protected void NotifyMenuChanged() => MenuStateChanged?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _screenCts.Cancel();
        _screenCts.Dispose();
        OnDeactivating();
        OnDispose();
        Content.Dispose();
    }

    /// <summary>
    /// Override for screen-specific cleanup. Called during Dispose after cancellation and hotkey cleanup.
    /// </summary>
    protected virtual void OnDispose() { }
}
