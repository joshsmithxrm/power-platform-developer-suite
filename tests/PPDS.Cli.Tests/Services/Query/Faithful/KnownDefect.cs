namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Markers for faithful SELECT cases that the engine gets wrong today. Each names the open issue that tracks the defect. A
/// marked case asserts that the engine still returns the pinned wrong result (<see cref="FaithfulSelect.Verdict"/>), so it
/// fails the day the engine changes; the fix for the issue then removes the marker. The T-SQL expectation of a marked case
/// must not be changed to match the engine.
/// </summary>
internal static class KnownDefect
{
    public const string ExpressionDropped = "Known defect #1612: a WHERE predicate on an expression is dropped (every row returned)";
    public const string NotInSubquery = "Known defect #1613: NOT IN (subquery) sends no filter and returns every row";
    public const string JoinedCondition = "Known defect #1614: a condition on a joined table's column is applied to the primary table";
    public const string NotLost = "Known defect #1615: NOT (...) in a WHERE sent to Dataverse is refused (before, it lost its NOT and selected the opposite rows)";
    public const string NegatedMatchesNull = "Known defect #1616: <>, !=, NOT IN and NOT LIKE return the rows where the column is NULL";
    public const string ExpressionInOr = "Known defect #1617: an expression comparison inside OR is dropped from the server filter";
    public const string NotEqualNull = "Known defect #1616, #1618: <> NULL is refused (before, it was sent as ne \"\" and returned every row)";
    /// <summary>
    /// Not a defect of the result: the maintainer decided (2026-11-24, "Refuse all seven") to refuse comparing with NULL,
    /// which T-SQL answers with no rows, because the translation behind it returned every row for <c>&lt;&gt; NULL</c>.
    /// </summary>
    public const string NullComparisonRefusedByDecision = "Refused by the maintainer's decision (2026-11-24): comparing with NULL; write IS NULL or IS NOT NULL";
    public const string VariableAsText = "Known defect #1618: a script variable in a WHERE sent to Dataverse is refused (before, it was sent as the literal text @n)";
    public const string TopParenthesised = "Known defect #1619: TOP (n) is ignored";
    public const string TopBeforeClientFilter = "Known defect #1619: TOP is applied before the client-side filter";
    public const string DateTimeNoOffset = "Known defect #1620: a datetime literal without an offset is read in the caller's time zone";
    public const string ComputedInputsNull = "Known defect #1621: an aliased column next to a computed column comes back NULL";
    public const string ChoiceOrderByLabel = "Known defect #1628: ORDER BY a choice column sorts by label, not value";

    /// <summary>
    /// For each marker, the replayed audit case that shows the same defect. When that case is marked <c>Fixed(...)</c> in
    /// <c>AuditCaseReplayTests.WrongCases</c>, a case with this marker must return its T-SQL result: the one edit there
    /// clears the marker here too.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Witness = new Dictionary<string, string>
    {
        [ExpressionDropped] = "W77", [NotInSubquery] = "S03", [JoinedCondition] = "J23", [NotLost] = "W09",
        [NegatedMatchesNull] = "W02", [ExpressionInOr] = "W89", [NotEqualNull] = "W25", [NullComparisonRefusedByDecision] = "W25", [VariableAsText] = "X01",
        [TopParenthesised] = "T02", [TopBeforeClientFilter] = "T07", [DateTimeNoOffset] = "W101",
        [ComputedInputsNull] = "PRJ05", [ChoiceOrderByLabel] = "O07",
    };
}
