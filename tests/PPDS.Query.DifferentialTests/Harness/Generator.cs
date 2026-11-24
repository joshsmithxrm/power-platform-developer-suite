namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// A generated expression: the rule or leaf that made it, its SQL text and its operands. A predicate is evaluated on
/// its own through <see cref="ScalarSql"/>, which turns its three-valued result into 1, 0 or NULL.
/// </summary>
public sealed record Expr(string Construct, Kind Kind, string Sql, IReadOnlyList<Expr> Children)
{
    /// <summary>True for a column or literal.</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>SQL text that is a scalar expression: the expression itself, or a predicate wrapped in CASE.</summary>
    public string ScalarSql => Kind == Kind.Predicate ? $"CASE WHEN {Sql} THEN 1 WHEN NOT ({Sql}) THEN 0 END" : Sql;

    /// <summary>This node and every node below it, parents first.</summary>
    public IEnumerable<Expr> Nodes() => Children.SelectMany(c => c.Nodes()).Prepend(this);

    /// <summary>A leaf.</summary>
    public static Expr Of(Leaf leaf) => new(leaf.Name, leaf.Kind, leaf.Sql, []);

    /// <summary>A rule applied to operands.</summary>
    public static Expr Of(Rule rule, params Expr[] children)
        => new(rule.Name, rule.Result, rule.Render(children.Select(c => c.Sql).ToArray()), children);
}

/// <summary>
/// A small deterministic random source (SplitMix64). Written out here, rather than <see cref="Random"/>, so that a seed
/// produces the same expressions on every .NET version and every machine.
/// </summary>
public sealed class SeededRandom(ulong seed)
{
    private ulong _state = seed;

    /// <summary>The next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A number from 0 to <paramref name="maxExclusive"/> - 1.</summary>
    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>One of <paramref name="items"/>, each with probability proportional to its weight.</summary>
    public T Pick<T>(IReadOnlyList<T> items, Func<T, int> weight)
    {
        var total = items.Sum(weight);
        var at = Next(total);
        foreach (var item in items)
        {
            at -= weight(item);
            if (at < 0)
                return item;
        }
        throw new InvalidOperationException("Unreachable: weights changed while picking.");
    }
}

/// <summary>
/// Generates expressions from the rule table. Deterministic: the same seed, depth and table give the same expressions
/// in the same order, so a seed printed by a failing run reproduces it.
/// </summary>
public sealed class ExpressionGenerator
{
    private readonly SeededRandom _random;
    private readonly int _maxDepth;
    private readonly IReadOnlyList<Leaf> _leaves;
    private readonly IReadOnlyList<Rule> _rules;

    /// <summary>A generator over the given table (the default table when null).</summary>
    public ExpressionGenerator(int seed, int maxDepth, IReadOnlyList<Leaf>? leaves = null, IReadOnlyList<Rule>? rules = null)
    {
        if (maxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "The depth must be at least 1.");
        _random = new SeededRandom(unchecked((ulong)seed));
        _maxDepth = maxDepth;
        _leaves = leaves ?? RuleTable.Leaves;
        _rules = rules ?? RuleTable.Rules;
    }

    /// <summary>The next expression: a number three times in four, text otherwise, of depth 1 to the maximum.</summary>
    public Expr Next()
    {
        var kind = _random.Next(4) == 0 ? Kind.Text : Kind.Number;
        return Generate(kind, 1 + _random.Next(_maxDepth), root: true);
    }

    private Expr Generate(Kind kind, int depth, bool root = false)
    {
        // A predicate is always a comparison; anything else becomes a leaf at the bottom, and sometimes earlier, so
        // that shallow operands appear under deep parents. The root is never a leaf.
        if (kind != Kind.Predicate && (depth <= 0 || (!root && _random.Next(4) == 0)))
        {
            var leaves = _leaves.Where(l => l.Kind == kind).ToList();
            return Expr.Of(leaves[_random.Next(leaves.Count)]);
        }

        var rules = _rules.Where(r => r.Result == kind).ToList();
        var rule = _random.Pick(rules, r => r.Weight);
        var children = rule.Operands.Select(k => Generate(k, depth - 1)).ToArray();
        return Expr.Of(rule, children);
    }
}
