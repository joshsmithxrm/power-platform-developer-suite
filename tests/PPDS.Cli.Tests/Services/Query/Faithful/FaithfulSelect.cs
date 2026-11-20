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
    {
        var expected = expectedKeys.Length == 0 ? [] : expectedKeys.Split(',');
        var actual = Column(result, "accountnumber").Select(v => v as string ?? "<null>").ToList();
        if (!ordered)
        {
            expected = expected.Order(StringComparer.Ordinal).ToArray();
            actual = actual.Order(StringComparer.Ordinal).ToList();
        }
        Assert.True(expected.SequenceEqual(actual),
            $"{sql}\n  expected: [{string.Join(",", expected)}]\n  actual:   [{string.Join(",", actual)}]");
    }
}
