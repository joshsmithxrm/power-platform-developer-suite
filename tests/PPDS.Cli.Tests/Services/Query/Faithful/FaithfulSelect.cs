using System.Globalization;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Runs a SELECT through the real <see cref="SqlQueryService"/> (parser, planner, FetchXML generator, executor) against
/// <see cref="SelectFixture"/> in a <see cref="FakeDataverse"/>, and asserts what the statement returned.
/// </summary>
internal static class FaithfulSelect
{
    public static async Task<QueryResult> RunAsync(string sql, FakeDataverse? dataverse = null)
    {
        dataverse ??= SelectFixture.Create();
        var result = await dataverse.CreateSqlQueryService().ExecuteAsync(new SqlQueryRequest { Sql = sql });
        return result.Result;
    }

    /// <summary>The values of <paramref name="column"/>, in the order returned. A key missing from a record is NULL.</summary>
    public static IReadOnlyList<object?> Column(QueryResult result, string column)
        => result.Records.Select(r => r.TryGetValue(column, out var v) ? v.Value : null).ToList();

    /// <summary>The <c>accountnumber</c> values, comma-separated, sorted unless <paramref name="ordered"/>.</summary>
    public static string Keys(QueryResult result, bool ordered)
    {
        var keys = Column(result, "accountnumber").Select(v => v as string ?? "<null>");
        return string.Join(",", ordered ? keys : keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The result rendered by <paramref name="render"/>, or "fault: ..." when Dataverse (the fake) or the engine raised an
    /// error. A refusal by the fake (<see cref="NotSupportedException"/>) is not an outcome and propagates.
    /// </summary>
    public static async Task<string> OutcomeAsync(string sql, Func<QueryResult, string> render, FakeDataverse? dataverse = null)
    {
        try
        {
            return render(await RunAsync(sql, dataverse));
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            return $"fault: {ex.Message.Split('\n')[0].Trim()}";
        }
    }

    /// <summary>
    /// Asserts that the statement returns exactly the accounts <paramref name="expectedKeys"/> (comma-separated
    /// <c>accountnumber</c> values; empty for none), in that order when <paramref name="ordered"/>.
    /// </summary>
    public static async Task AssertKeysAsync(string sql, string expectedKeys, bool ordered = false, FakeDataverse? dataverse = null)
    {
        var result = await RunAsync(sql, dataverse);
        AssertKeys(result, expectedKeys, ordered, sql);
    }

    public static void AssertKeys(QueryResult result, string expectedKeys, bool ordered, string sql)
        => Verdict(sql, Sort(expectedKeys, ordered), Keys(result, ordered), defect: null, today: null);

    /// <summary>Sorts a comma-separated expectation the way <see cref="Keys"/> sorts an unordered result.</summary>
    public static string Sort(string keys, bool ordered)
        => ordered || keys.Length == 0 ? keys : string.Join(",", keys.Split(',').Order(StringComparer.Ordinal));

    /// <summary>
    /// With no <paramref name="defect"/>: the outcome must be <paramref name="expected"/>, the T-SQL result.
    /// With a known defect: the outcome must still be <paramref name="today"/>, the wrong result the engine returns today, so
    /// this test fails the day the engine changes. When it then returns <paramref name="expected"/>, the defect is fixed and
    /// the marker must be removed.
    /// </summary>
    public static void Verdict(string sql, string expected, string actual, string? defect, string? today)
    {
        // A marker whose witness audit case is marked fixed is cleared: the case must now return its T-SQL result.
        if (defect is not null && AuditCaseReplayTests.IsFixedSinceTheAudit(KnownDefect.Witness[defect]))
            defect = null;
        if (defect is null)
        {
            Assert.True(expected == actual, $"{sql}\n  expected: [{expected}]\n  actual:   [{actual}]");
            return;
        }

        Assert.True(today != expected, $"{defect}: the pinned result equals the T-SQL result; this is not a defect.");
        Assert.True(actual == today,
            $"{defect}\n{sql}\n  T-SQL result:   [{expected}]\n  pinned today:   [{today}]\n  engine returns: [{actual}]\n" +
            (actual == expected
                ? $"The defect is fixed: in AuditCaseReplayTests.WrongCases change the witness case [\"{KnownDefect.Witness[defect]}\"] from Wrong(...) to Fixed(...); that one edit clears this marker too."
                : "The engine's wrong result changed: check why, then update the pin."));
    }

    public static string Values(QueryResult result, params string[] columns)
        => string.Join(";", result.Records.Select(r => string.Join(",", columns.Select(c =>
            r.TryGetValue(c, out var v) && v.Value is not null ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : "<null>"))));
}
