namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// One hand-written case of a known difference, with both engines' outcomes pinned exactly in
/// <see cref="Outcome.ToString"/> form. The SQL Server outcome is what the documentation says and the reference server
/// returns; the PPDS outcome is the wrong result the engine returns today.
/// </summary>
/// <param name="Expression">The expression, evaluated over <paramref name="RowId"/> of the operand table.</param>
/// <param name="RowId">The operand row (1 when the expression reads no column).</param>
/// <param name="SqlServer">The reference outcome.</param>
/// <param name="PpdsToday">The engine's outcome today.</param>
public sealed record KnownCase(string Expression, int RowId, string SqlServer, string PpdsToday);

/// <summary>
/// A difference between the engine and SQL Server that an open issue records, with hand-written cases whose outcomes
/// are pinned exactly (the negative control). Nothing is matched against it: it is a list of exact records.
/// </summary>
/// <param name="Id">A short stable id.</param>
/// <param name="Issue">The issue that records it, and where on it.</param>
/// <param name="Summary">The defect, in one sentence.</param>
/// <param name="Cases">The hand-written cases.</param>
public sealed record KnownDifference(string Id, string Issue, string Summary, IReadOnlyList<KnownCase> Cases)
{
    /// <inheritdoc />
    public override string ToString() => $"{Id} ({Issue})";
}
