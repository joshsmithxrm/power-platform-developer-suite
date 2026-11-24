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
public sealed record KnownCase(string Expression, int RowId, string SqlServer, string PpdsToday);

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
/// True when a cause found in a generated expression is this defect. It must be narrow: it sees the construct, the
/// operands' outcomes and both results, and it must not accept a difference of another kind on the same construct.
/// </param>
public sealed record KnownDifference(string Id, string Issue, string Summary, IReadOnlyList<KnownCase> Cases, Func<NodeDifference, bool> Matches)
{
    /// <summary>True when <paramref name="difference"/> is this known defect.</summary>
    public bool Explains(NodeDifference difference) => Matches(difference);

    /// <inheritdoc />
    public override string ToString() => $"{Id} ({Issue})";
}
