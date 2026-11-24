using System.Globalization;
using System.Text;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>One node of an expression evaluated by both engines over one row.</summary>
public sealed record NodeResult(Expr Node, Outcome Reference, Outcome Actual, Difference Difference);

/// <summary>
/// A node that differs while every one of its operands agrees: the construct itself computes something other than
/// SQL Server, from the same inputs. This is what a known difference is matched against.
/// </summary>
public sealed record NodeDifference(Expr Node, int RowId, Outcome Reference, Outcome Actual, Difference Difference, IReadOnlyList<NodeResult> Children)
{
    /// <summary>
    /// The group a new difference is reported under: the construct, its operands' SQL types, and what differs.
    /// </summary>
    public string Signature
    {
        get
        {
            var operands = Node.IsLeaf
                ? ""
                : "(" + string.Join(", ", Children.Select(c => c.Node.IsLeaf ? c.Node.Construct : c.Reference.Type?.Name ?? (c.Node.Kind == Kind.Predicate ? "predicate" : "error"))) + ")";
            return $"{Node.Construct}{operands}: {Describe(Reference, Actual, Difference)}";
        }
    }

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

/// <summary>The verdict on one expression over one row.</summary>
public sealed record CaseVerdict(
    Expr Expression,
    int RowId,
    NodeResult Root,
    IReadOnlyList<(NodeDifference Difference, KnownDifference? Known)> Causes)
{
    /// <summary>The engines agree on value, error and type.</summary>
    public bool IsEqual => Root.Difference == Difference.None;

    /// <summary>The engines differ, and every cause is a known difference.</summary>
    public bool IsExpected => !IsEqual && Causes.All(c => c.Known is not null);

    /// <summary>The engines differ, and at least one cause is not a known difference.</summary>
    public bool IsNew => !IsEqual && Causes.Any(c => c.Known is null);
}

/// <summary>
/// Runs expressions through the reference and the engine under test and classifies every difference.
/// </summary>
/// <remarks>
/// <para>
/// When the whole expression differs, the runner evaluates every node of it on both engines and finds the smallest
/// nodes that differ while all their operands agree. Each such node is a cause; it is either explained by a known
/// difference (<see cref="KnownDifferences"/>) or it is new. An expression counts as an expected difference only when
/// every cause is known, so a known difference cannot hide a new one beside it.
/// </para>
/// <para>
/// A node above a cause has inputs that already differ, so a second defect in that node cannot be seen in that
/// expression. The generator also produces that construct directly over columns and literals, where its inputs agree,
/// and a defect there is a cause of its own.
/// </para>
/// </remarks>
public sealed class DifferentialRunner(IExpressionEngine reference, IExpressionEngine actual, IReadOnlyList<KnownDifference> known)
{
    private readonly Dictionary<(string Sql, int Row), NodeResult> _cache = new();

    /// <summary>Evaluates one node over one row on both engines (cached: subexpressions repeat).</summary>
    public NodeResult Evaluate(Expr node, int rowId)
    {
        var key = (node.ScalarSql, rowId);
        if (_cache.TryGetValue(key, out var cached))
            return cached with { Node = node };
        var r = reference.Evaluate(node.ScalarSql, rowId);
        var a = actual.Evaluate(node.ScalarSql, rowId);
        var result = new NodeResult(node, r, a, Outcome.Compare(r, a));
        _cache[key] = result;
        return result;
    }

    /// <summary>Checks one expression over one row.</summary>
    public CaseVerdict Check(Expr expression, int rowId)
    {
        var root = Evaluate(expression, rowId);
        if (root.Difference == Difference.None)
            return new CaseVerdict(expression, rowId, root, []);

        var causes = Causes(expression, rowId)
            .Select(d => (d, known.FirstOrDefault(k => k.Explains(d))))
            .ToList();
        return new CaseVerdict(expression, rowId, root, causes);
    }

    private List<NodeDifference> Causes(Expr node, int rowId)
    {
        var self = Evaluate(node, rowId);
        if (self.Difference == Difference.None)
            return [];
        var below = node.Children.SelectMany(c => Causes(c, rowId)).ToList();
        if (below.Count > 0)
            return below;
        var children = node.Children.Select(c => Evaluate(c, rowId)).ToList();
        return [new NodeDifference(node, rowId, self.Reference, self.Actual, self.Difference, children)];
    }

    /// <summary>Generates <paramref name="count"/> expressions from <paramref name="seed"/> and checks each over every row.</summary>
    public RunReport Run(int seed, int count, int maxDepth, IReadOnlyList<int> rowIds)
    {
        var report = new RunReport(seed, count, maxDepth);
        var generator = new ExpressionGenerator(seed, maxDepth);
        for (var i = 0; i < count; i++)
        {
            var expression = generator.Next();
            foreach (var rowId in rowIds)
                report.Add(i, Check(expression, rowId));
        }
        return report;
    }
}

/// <summary>The counts and the new differences of one generated run.</summary>
public sealed class RunReport(int seed, int expressions, int maxDepth)
{
    private readonly SortedDictionary<string, int> _expected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NewDifferenceGroup> _new = new(StringComparer.Ordinal);

    /// <summary>The seed.</summary>
    public int Seed { get; } = seed;

    /// <summary>The number of generated expressions.</summary>
    public int Expressions { get; } = expressions;

    /// <summary>The maximum depth.</summary>
    public int MaxDepth { get; } = maxDepth;

    /// <summary>Expressions times rows.</summary>
    public int Evaluations { get; private set; }

    /// <summary>Evaluations where the engines agree.</summary>
    public int Equal { get; private set; }

    /// <summary>Evaluations that differ only by known differences.</summary>
    public int Expected { get; private set; }

    /// <summary>Evaluations with at least one new difference.</summary>
    public int New { get; private set; }

    /// <summary>Known differences by id, counted once per evaluation they explain.</summary>
    public IReadOnlyDictionary<string, int> ExpectedById => _expected;

    /// <summary>The new differences, grouped by <see cref="NodeDifference.Signature"/>, most frequent first.</summary>
    public IReadOnlyList<NewDifferenceGroup> NewGroups => _new.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Signature, StringComparer.Ordinal).ToList();

    /// <summary>Adds one verdict.</summary>
    public void Add(int index, CaseVerdict verdict)
    {
        Evaluations++;
        if (verdict.IsEqual)
        {
            Equal++;
            return;
        }
        if (verdict.IsExpected)
            Expected++;
        else
            New++;

        foreach (var (difference, known) in verdict.Causes)
        {
            if (known is not null)
            {
                _expected[known.Id] = _expected.GetValueOrDefault(known.Id) + 1;
                continue;
            }
            if (!_new.TryGetValue(difference.Signature, out var group))
                _new[difference.Signature] = group = new NewDifferenceGroup(difference.Signature, Seed, index, verdict, difference);
            group.Count++;
        }
    }

    /// <summary>The report as text: counts, then each new difference with a reproducible example.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Seed {Seed}, depth up to {MaxDepth}: {Expressions} expressions over {Operands.Rows.Count} rows = {Evaluations} evaluations.");
        text.AppendLine(CultureInfo.InvariantCulture, $"  equal {Equal}, expected differences {Expected}, new differences {New}.");
        foreach (var (id, n) in _expected)
            text.AppendLine(CultureInfo.InvariantCulture, $"  expected {id}: {n}");
        foreach (var group in NewGroups)
            text.Append(group);
        return text.ToString();
    }
}

/// <summary>New differences that share a signature, with the first example found.</summary>
public sealed class NewDifferenceGroup(string signature, int seed, int index, CaseVerdict example, NodeDifference cause)
{
    /// <summary>The signature.</summary>
    public string Signature { get; } = signature;

    /// <summary>How many causes had it.</summary>
    public int Count { get; set; }

    /// <summary>The seed of the run that found it.</summary>
    public int Seed { get; } = seed;

    /// <summary>The 0-based index of the first expression of the run that showed it.</summary>
    public int Index { get; } = index;

    /// <summary>The first evaluation that showed it.</summary>
    public CaseVerdict Example { get; } = example;

    /// <summary>The cause in that evaluation.</summary>
    public NodeDifference Cause { get; } = cause;

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"  NEW [{Count}x] {Signature}");
        text.AppendLine(CultureInfo.InvariantCulture, $"      cause:  {Cause.Node.ScalarSql}   (row {Cause.RowId})");
        text.AppendLine(CultureInfo.InvariantCulture, $"        SQL Server: {Cause.Reference}");
        text.AppendLine(CultureInfo.InvariantCulture, $"        PPDS:       {Cause.Actual}");
        if (!ReferenceEquals(Example.Expression, Cause.Node))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"      in:     {Example.Expression.ScalarSql}");
            text.AppendLine(CultureInfo.InvariantCulture, $"        SQL Server: {Example.Root.Reference}");
            text.AppendLine(CultureInfo.InvariantCulture, $"        PPDS:       {Example.Root.Actual}");
        }
        text.AppendLine(CultureInfo.InvariantCulture, $"      reproduce: seed {Seed}, expression {Index}");
        return text.ToString();
    }
}
