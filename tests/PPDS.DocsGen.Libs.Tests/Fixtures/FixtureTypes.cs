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
/// Exercises the &lt;inheritdoc /&gt; fallback path: the type-level summary
/// resolves against <see cref="IWidget"/>, but the <c>DoExternalWork</c> method
/// points at a cref that is not present in the XML map. The generator must
/// emit a fallback pointer instead of silently dropping the member.
/// </summary>
public sealed class ExternalDocsConsumer
{
    /// <inheritdoc cref="T:External.Unresolvable.Api.SomeThing" />
    public void DoExternalWork() { }
}

/// <summary>
/// Generic processor that exercises MDX angle-bracket escaping in headings
/// and type references.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class GenericProcessor<TRequest, TResponse>
{
    /// <summary>
    /// Creates a new processor with the given handler.
    /// </summary>
    /// <param name="handler">A function that maps requests to responses.</param>
    public GenericProcessor(Func<TRequest, TResponse> handler)
    {
        Handler = handler;
    }

    /// <summary>
    /// The handler function.
    /// </summary>
    public Func<TRequest, TResponse> Handler { get; }

    /// <summary>
    /// Processes a single request.
    /// </summary>
    /// <param name="request">The request to process.</param>
    /// <returns>The response produced by the handler.</returns>
    public TResponse Process(TRequest request) => Handler(request);

    /// <summary>
    /// Processes a batch of requests and returns all responses.
    /// </summary>
    /// <param name="requests">The batch of requests.</param>
    /// <returns>A list of responses.</returns>
    public List<TResponse> ProcessBatch(IEnumerable<TRequest> requests) =>
        requests.Select(Handler).ToList();
}

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
