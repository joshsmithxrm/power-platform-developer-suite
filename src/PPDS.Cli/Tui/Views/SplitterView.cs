using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// A horizontal splitter bar that can be dragged with the mouse to resize panels above and below.
/// Renders as a horizontal line (U+2500) and fires <see cref="Dragged"/> when the user drags it.
/// </summary>
internal sealed class SplitterView : View
{
    /// <summary>
    /// The horizontal line character used to render the splitter bar.
    /// </summary>
    private const char SplitterChar = '\u2500'; // ─

    /// <summary>
    /// Fired when the user drags the splitter. The argument is the delta in rows
    /// (positive = dragged down, negative = dragged up).
    /// </summary>
    public event Action<int>? Dragged;

    private int _dragStartScreenY;
    private bool _isDragging;

    public SplitterView()
    {
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;
    }

    /// <inheritdoc />
    public override void Redraw(Rect bounds)
    {
        Clear();
        Move(0, 0);

        var attr = _isDragging
            ? Application.Driver.MakeAttribute(Color.Black, Color.Cyan)
            : Application.Driver.MakeAttribute(Color.DarkGray, Color.Black);

        Driver.SetAttribute(attr);
        for (int i = 0; i < bounds.Width; i++)
        {
            Driver.AddRune(SplitterChar);
        }
    }

    /// <inheritdoc />
    public override bool MouseEvent(MouseEvent ev)
    {
        if (ev.Flags.HasFlag(MouseFlags.Button1Pressed))
        {
            // Start drag - capture absolute screen Y
            _isDragging = true;
            Application.GrabMouse(this);
            _dragStartScreenY = ev.Y;
            SetNeedsDisplay();
            return true;
        }

        if (ev.Flags.HasFlag(MouseFlags.Button1Released))
        {
            if (_isDragging)
            {
                _isDragging = false;
                Application.UngrabMouse();
                SetNeedsDisplay();
            }
            return true;
        }

        if (_isDragging && ev.Flags.HasFlag(MouseFlags.ReportMousePosition))
        {
            // ev.Y is relative to this view; delta from drag start
            var delta = ev.Y - _dragStartScreenY;
            if (delta != 0)
            {
                Dragged?.Invoke(delta);
                // Reset baseline so further movement is incremental
                // (the view moves, so ev.Y base shifts)
                _dragStartScreenY = ev.Y;
            }
            return true;
        }

        return base.MouseEvent(ev);
    }
}
