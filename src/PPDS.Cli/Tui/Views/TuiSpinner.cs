using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Animated braille spinner for indicating async operations in progress.
/// </summary>
/// <remarks>
/// <para>
/// Use TuiSpinner for any async operation expected to take more than ~500ms.
/// The spinner provides visual feedback that the application is working.
/// </para>
/// <para>
/// Usage:
/// <code>
/// var spinner = new TuiSpinner { X = 1, Y = 1 };
/// Add(spinner);
/// spinner.Start("Loading...");
/// // ... async operation ...
/// spinner.Stop();
/// </code>
/// </para>
/// <para>
/// See docs/patterns/tui-loading.md for usage guidelines.
/// </para>
/// </remarks>
public sealed class TuiSpinner : View
{
    /// <summary>
    /// Braille spinner animation frames (Unicode Braille Pattern Dots).
    /// </summary>
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    /// <summary>
    /// Animation interval in milliseconds.
    /// </summary>
    private const int AnimationIntervalMs = 100;

    private int _frameIndex;
    private object? _timer;
    private string _message = string.Empty;
    private bool _isRunning;
    private string _displayText = string.Empty;

    /// <summary>
    /// Gets or sets the message displayed after the spinner.
    /// </summary>
    public string Message
    {
        get => _message;
        set
        {
            _message = value ?? string.Empty;
            UpdateDisplayText();
        }
    }

    /// <summary>
    /// Gets whether the spinner is currently animating.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Creates a new TuiSpinner.
    /// </summary>
    public TuiSpinner()
    {
        Height = 1;
        Width = Dim.Fill();
    }

    /// <summary>
    /// Starts the spinner animation.
    /// </summary>
    /// <param name="message">Optional message to display after the spinner.</param>
    public void Start(string? message = null)
    {
        if (_isRunning)
        {
            // Already running, just update message
            if (message != null)
            {
                Message = message;
            }
            return;
        }

        _message = message ?? _message;
        _isRunning = true;
        _frameIndex = 0;
        Visible = true;

        UpdateDisplayText();

        // Start animation timer
        _timer = Application.MainLoop?.AddTimeout(
            TimeSpan.FromMilliseconds(AnimationIntervalMs),
            OnTimerTick);
    }

    /// <summary>
    /// Stops the spinner animation.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;

        if (_timer != null)
        {
            Application.MainLoop?.RemoveTimeout(_timer);
            _timer = null;
        }

        _displayText = string.Empty;
        Visible = false;
        SetNeedsDisplay();
    }

    /// <summary>
    /// Stops the spinner and displays a final message.
    /// </summary>
    /// <param name="finalMessage">The message to display.</param>
    public void StopWithMessage(string finalMessage)
    {
        _isRunning = false;

        if (_timer != null)
        {
            Application.MainLoop?.RemoveTimeout(_timer);
            _timer = null;
        }

        // Show final message without spinner
        _message = finalMessage;
        _displayText = finalMessage;
        Visible = true;
        SetNeedsDisplay();
    }

    /// <inheritdoc />
    public override void Redraw(Rect bounds)
    {
        Clear();

        if (!string.IsNullOrEmpty(_displayText))
        {
            Move(0, 0);
            Driver.AddStr(_displayText);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
        base.Dispose(disposing);
    }

    private bool OnTimerTick(MainLoop mainLoop)
    {
        if (!_isRunning)
        {
            return false; // Stop the timer
        }

        _frameIndex = (_frameIndex + 1) % Frames.Length;
        UpdateDisplayText();

        return true; // Continue the timer
    }

    private void UpdateDisplayText()
    {
        if (_isRunning)
        {
            var spinnerChar = Frames[_frameIndex];
            _displayText = string.IsNullOrEmpty(_message)
                ? spinnerChar
                : $"{spinnerChar} {_message}";
        }
        else if (!string.IsNullOrEmpty(_message))
        {
            _displayText = _message;
        }
        else
        {
            _displayText = string.Empty;
        }

        SetNeedsDisplay();
    }
}
