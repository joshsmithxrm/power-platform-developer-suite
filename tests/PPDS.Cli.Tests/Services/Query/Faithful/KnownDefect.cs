namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Skip reasons for faithful SELECT tests that fail because the engine returns the wrong result today. Each names the
/// open issue that tracks the defect; the fix for that issue removes the skip. The expectation of a skipped test is what
/// T-SQL returns and must not be changed to match the engine.
/// </summary>
internal static class KnownDefect
{
    public const string ExpressionDropped = "Known defect #1612: a WHERE predicate on an expression is dropped (every row returned)";
    public const string NotInSubquery = "Known defect #1613: NOT IN (subquery) sends no filter and returns every row";
    public const string JoinedCondition = "Known defect #1614: a condition on a joined table's column is applied to the primary table";
    public const string NotLost = "Known defect #1615: NOT (...) loses its NOT and selects the opposite rows";
    public const string NegatedMatchesNull = "Known defect #1616: <>, !=, NOT IN and NOT LIKE return the rows where the column is NULL";
    public const string ExpressionInOr = "Known defect #1617: an expression comparison inside OR is dropped from the server filter";
    public const string NotEqualNull = "Known defect #1616, #1618: <> NULL is sent as ne \"\" and returns every row";
    public const string VariableAsText = "Known defect #1618: a script variable is sent as the literal text @n";
    public const string TopParenthesised = "Known defect #1619: TOP (n) is ignored";
    public const string TopBeforeClientFilter = "Known defect #1619: TOP is applied before the client-side filter";
    public const string DateTimeNoOffset = "Known defect #1620: a datetime literal without an offset is read in the caller's time zone";
    public const string ComputedInputsNull = "Known defect #1621: an aliased column next to a computed column comes back NULL";
    public const string ChoiceOrderByLabel = "Known defect #1628: ORDER BY a choice column sorts by label, not value";
}
