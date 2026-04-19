using System.ComponentModel;

namespace PPDS.DocsGen.Libs.Tests.FixtureLib;

/// <summary>
/// Example public interface used by the libs-reflect fixture.
/// </summary>
public interface IWidget
{
    /// <summary>
    /// The widget's display name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Activates the widget for the given count.
    /// </summary>
    /// <param name="count">The number of times to activate.</param>
    /// <returns>The total number of activations performed.</returns>
    int Activate(int count);
}

/// <summary>
/// Simple widget implementation. Has a constructor and a couple of members.
/// </summary>
public sealed class Widget : IWidget
{
    /// <summary>
    /// Creates a new widget with the given display name.
    /// </summary>
    /// <param name="name">The name to assign.</param>
    public Widget(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The widget's display name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Activates the widget for the given count.
    /// </summary>
    /// <param name="count">The number of times to activate.</param>
    /// <returns>The total number of activations performed.</returns>
    public int Activate(int count) => count;
}

/// <summary>
/// Intentionally hidden helper. Marked [EditorBrowsable(Never)] so libs-reflect
/// must silently skip it without logging a diagnostic.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class HiddenHelper
{
    /// <summary>Does a thing.</summary>
    public void DoThing() { }
}

public sealed class UndocumentedThing
{
    public int Value { get; set; }
}

/// <summary>
/// Lightweight value container used to exercise record emission.
/// </summary>
/// <param name="Id">Stable identifier for the measurement.</param>
/// <param name="Label">Human-readable label.</param>
public sealed record Measurement(int Id, string Label);

/// <summary>
/// Possible states for a fixture widget.
/// </summary>
public enum WidgetState
{
    /// <summary>The widget has not yet been initialized.</summary>
    Uninitialized = 0,

    /// <summary>The widget is currently active.</summary>
    Active = 1,

    /// <summary>The widget has been retired and will not respond.</summary>
    Retired = 2,
}
