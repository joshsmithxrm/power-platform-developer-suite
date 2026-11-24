namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// One hand-written case of a known difference, with both engines' outcomes pinned in <see cref="Outcome.ToString"/>
/// form. The SQL Server outcome is what the documentation says and the reference server returns; the PPDS outcome is
/// the wrong result the engine returns today.
/// </summary>
/// <param name="Expression">The expression, evaluated over <paramref name="RowId"/> of the operand table.</param>
/// <param name="RowId">The operand row (1 when the expression reads no column).</param>
/// <param name="SqlServer">The reference outcome.</param>
/// <param name="PpdsToday">The engine's outcome today.</param>
/// <param name="Tree">
/// The same expression built from harness nodes, so that the runner can find its cause and check that this entry's own
/// matcher explains it. Its SQL text may differ from <paramref name="Expression"/> only in parentheses.
/// </param>
public sealed record KnownCase(string Expression, int RowId, string SqlServer, string PpdsToday, Expr Tree);

/// <summary>
/// A difference between the engine and SQL Server that an open issue already records. It has hand-written cases
/// (the negative control: each must still differ exactly as pinned) and a matcher that recognises the same defect as
/// the cause of a difference in a generated expression.
/// </summary>
/// <param name="Id">A short stable id.</param>
/// <param name="Issue">The issue that records it, and where on it.</param>
/// <param name="Summary">The defect, in one sentence.</param>
/// <param name="Cases">The hand-written cases.</param>
/// <param name="Matches">
/// True when a cause found in a generated expression is this defect. It must be narrow: it recomputes the defect's own
/// arithmetic from the operands' outcomes (the value the defect produces, and the value SQL Server produces), so that
/// a different defect on the same construct stays new. Each entry has near-miss tests that must stay unexplained.
/// </param>
public sealed record KnownDifference(string Id, string Issue, string Summary, IReadOnlyList<KnownCase> Cases, Func<NodeDifference, bool> Matches)
{
    /// <summary>True when <paramref name="difference"/> is this known defect.</summary>
    public bool Explains(NodeDifference difference) => Matches(difference);

    /// <inheritdoc />
    public override string ToString() => $"{Id} ({Issue})";
}
