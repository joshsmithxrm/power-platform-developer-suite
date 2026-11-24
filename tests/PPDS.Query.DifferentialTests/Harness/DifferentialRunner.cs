using System.Globalization;
using System.Text;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>One expression evaluated by both engines over one operand row.</summary>
/// <param name="Expression">The expression.</param>
/// <param name="RowId">The operand row.</param>
/// <param name="Reference">SQL Server's outcome.</param>
/// <param name="Actual">The engine's outcome.</param>
/// <param name="Difference">What differs.</param>
public sealed record Evaluation(Expr Expression, int RowId, Outcome Reference, Outcome Actual, Difference Difference)
{
    /// <summary>True when the engines agree on value, error and type.</summary>
    public bool IsEqual => Difference == Difference.None;
}

/// <summary>
/// The smallest node of a differing expression that differs while all its operands agree, for reading a report. It is
/// a reading aid only: nothing passes or fails on it.
/// </summary>
public sealed record NodeDifference(Expr Node, int RowId, Outcome Reference, Outcome Actual, Difference Difference, IReadOnlyList<Evaluation> Children)
{
    /// <summary>The construct, its operands' SQL types, and what differs.</summary>
    public string Signature
    {
        get
        {
            var operands = Node.IsLeaf
                ? ""
                : "(" + string.Join(", ", Children.Select(c => c.Expression.IsLeaf ? c.Expression.Construct : c.Reference.Type?.Name ?? (c.Expression.Kind == Kind.Predicate ? "predicate" : "error"))) + ")";
            return $"{Node.Construct}{operands}: {Describe(Reference, Actual, Difference)}";
        }
    }

    /// <summary>The construct and what differs, the heading a report groups under.</summary>
    public string Group => $"{Node.Construct}: {Describe(Reference, Actual, Difference)}";

    /// <summary>What differs, in words.</summary>
    public static string Describe(Outcome reference, Outcome actual, Difference difference)
    {
        if (difference.HasFlag(Difference.Error))
        {
            return (reference.IsError, actual.IsError) switch
            {
                (true, false) => $"PPDS returns a value where SQL Server raises {reference.Error}",
                (false, true) => $"PPDS raises {actual.Error} where SQL Server returns a value",
                _ => $"PPDS raises {actual.Error} where SQL Server raises {reference.Error}",
            };
        }

        var parts = new List<string>();
        if (difference.HasFlag(Difference.Value))
            parts.Add(Outcome.NumericallyEqual(reference.Value, actual.Value) ? "scale differs" : "value differs");
        if (difference.HasFlag(Difference.Type))
            parts.Add($"type {reference.Type?.Family} vs {actual.Type?.ToString() ?? actual.Family}");
        return string.Join(", ", parts);
    }
}

/// <summary>Runs generated expressions through both engines.</summary>
public sealed class DifferentialRunner(IExpressionEngine reference, IExpressionEngine actual)
{
    private readonly Dictionary<(string Sql, int Row), Evaluation> _cache = new();

    /// <summary>How many times SQL Server was asked to evaluate an expression.</summary>
    public int ReferenceCalls { get; private set; }

    /// <summary>How many times the engine under test was asked to evaluate an expression.</summary>
    public int ActualCalls { get; private set; }

    /// <summary>Evaluates one expression over one row on both engines.</summary>
    public Evaluation Evaluate(Expr expression, int rowId)
    {
        ReferenceCalls++;
        var r = reference.Evaluate(expression.ScalarSql, rowId);
        ActualCalls++;
        var a = actual.Evaluate(expression.ScalarSql, rowId);
        return new Evaluation(expression, rowId, r, a, Outcome.Compare(r, a));
    }

    /// <summary>
    /// The expressions <paramref name="seed"/> generates, each over every row (over the first row only when it reads
    /// no column, because then it does not depend on the row), with its index in the generated sequence.
    /// </summary>
    public IEnumerable<(int Index, Evaluation Evaluation)> Run(int seed, int count, int maxDepth, IReadOnlyList<int> rowIds)
    {
        var generator = new ExpressionGenerator(seed, maxDepth);
        for (var i = 0; i < count; i++)
        {
            var expression = generator.Next();
            var readsColumn = expression.Nodes().Any(n => n.IsLeaf && n.Construct.StartsWith("column ", StringComparison.Ordinal));
            foreach (var rowId in readsColumn ? rowIds : rowIds.Take(1))
                yield return (i, Evaluate(expression, rowId));
        }
    }

    /// <summary>
    /// For a report: the smallest nodes of a differing expression that differ while all their operands agree. Such a
    /// node may sit in an IIF or CASE branch SQL Server did not take, and a node above it is not looked at, so this
    /// names a place to start reading, not the cause.
    /// </summary>
    public IReadOnlyList<NodeDifference> Attribute(Expr node, int rowId)
    {
        var self = Cached(node, rowId);
        if (self.IsEqual)
            return [];
        var below = node.Children.SelectMany(c => Attribute(c, rowId)).ToList();
        if (below.Count > 0)
            return below;
        return [new NodeDifference(node, rowId, self.Reference, self.Actual, self.Difference, node.Children.Select(c => Cached(c, rowId)).ToList())];
    }

    private Evaluation Cached(Expr node, int rowId)
    {
        if (!_cache.TryGetValue((node.ScalarSql, rowId), out var evaluation))
            _cache[(node.ScalarSql, rowId)] = evaluation = Evaluate(node, rowId);
        return evaluation with { Expression = node };
    }
}

/// <summary>
/// A report for reading: counts, and the differences grouped by the smallest differing node. It passes or fails
/// nothing.
/// </summary>
public sealed class RunReport(int seed, int expressions, int maxDepth)
{
    private readonly Dictionary<string, (int Count, int Index, Evaluation Example, NodeDifference Node)> _groups = new(StringComparer.Ordinal);

    /// <summary>What the grouping means, printed with every report.</summary>
    public const string Attribution =
        "Grouping: each differing evaluation is listed under the smallest node that differs while its operands agree. "
        + "That node may sit in a branch SQL Server did not take, and a node above it is not looked at, so a group names "
        + "a place to start reading, not a cause. Nothing passes or fails on a group.";

    /// <summary>Evaluations run.</summary>
    public int Evaluations { get; private set; }

    /// <summary>Evaluations where the engines agree.</summary>
    public int Equal { get; private set; }

    /// <summary>Evaluations that differ.</summary>
    public int Different => Evaluations - Equal;

    /// <summary>Adds one evaluation, attributing it when it differs.</summary>
    public void Add(int index, Evaluation evaluation, DifferentialRunner runner)
    {
        Evaluations++;
        if (evaluation.IsEqual)
        {
            Equal++;
            return;
        }
        foreach (var node in runner.Attribute(evaluation.Expression, evaluation.RowId))
        {
            _groups[node.Group] = _groups.TryGetValue(node.Group, out var g)
                ? g with { Count = g.Count + 1 }
                : (1, index, evaluation, node);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Seed {seed}, depth up to {maxDepth}: {expressions} expressions, {Evaluations} evaluations; equal {Equal}, different {Different}; {_groups.Count} groups.");
        text.AppendLine("  " + Attribution);
        foreach (var (group, (count, index, example, node)) in _groups.OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  [{count}x] {group}");
            text.AppendLine(CultureInfo.InvariantCulture, $"      at:  {node.Node.ScalarSql}   (row {node.RowId})   SQL Server: {node.Reference}   PPDS: {node.Actual}");
            if (!ReferenceEquals(example.Expression, node.Node))
                text.AppendLine(CultureInfo.InvariantCulture, $"      in:  {example.Expression.ScalarSql}   SQL Server: {example.Reference}   PPDS: {example.Actual}");
            text.AppendLine(CultureInfo.InvariantCulture, $"      reproduce: seed {seed}, expression {index}");
        }
        return text.ToString();
    }
}
