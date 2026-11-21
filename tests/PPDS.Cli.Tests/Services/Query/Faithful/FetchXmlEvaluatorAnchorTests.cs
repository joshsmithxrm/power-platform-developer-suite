using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// A LIKE-family condition matches the whole stored value. In .NET a regular expression's <c>$</c> also matches before a
/// final line feed, so an end anchored with <c>$</c> made <c>like 'Beta'</c> and <c>ends-with 'a'</c> match a stored
/// "Beta\n" (and the negated forms drop it); the evaluator anchors with <c>\A</c> and <c>\z</c>.
/// <para>
/// No recorded case stores a value that ends in a line break (the audit observed a line break inside a memo, W125 and W126,
/// so <c>\n</c> is a character the fake compares). These tests assert full-value matching, not Dataverse behaviour. A
/// carriage return is a character whose comparison the audit never observed, so a value ending in "\r\n" is refused.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class FetchXmlEvaluatorAnchorTests
{
    private static FakeDataverse Dataverse(params string[] names)
    {
        var table = new FakeTable("t").Column("k", FakeColumnType.Integer).Column("name", FakeColumnType.String);
        for (var i = 0; i < names.Length; i++)
            table.Row(("k", i + 1), ("name", names[i]));
        return new FakeDataverse().Add(table);
    }

    private static string Keys(FakeDataverse dataverse, string op, string value)
        => string.Join(",", dataverse.RetrieveMultiple(
                $"<fetch><entity name=\"t\"><attribute name=\"k\" /><filter><condition attribute=\"name\" operator=\"{op}\" value=\"{value}\" /></filter></entity></fetch>")
            .Entities.Select(e => e.GetAttributeValue<int>("k")).Order());

    // 1 "Beta", 2 "Beta\n", 3 "Gamma"
    [Theory]
    [InlineData("like", "Beta", "1")]
    [InlineData("not-like", "Beta", "2,3")]
    [InlineData("like", "%a", "1,3")]
    [InlineData("not-like", "%a", "2")]
    [InlineData("begins-with", "Beta", "1,2")]
    [InlineData("not-begin-with", "Beta", "3")]
    [InlineData("ends-with", "a", "1,3")]
    [InlineData("not-end-with", "a", "2")]
    public void ALikeFamilyCondition_MatchesTheWholeValue_EvenWhenItEndsInALineFeed(string op, string value, string expected)
        => Assert.Equal(expected, Keys(Dataverse("Beta", "Beta\n", "Gamma"), op, value));

    [Theory]
    [InlineData("like", "Beta")]
    [InlineData("not-like", "Beta")]
    [InlineData("begins-with", "Beta")]
    [InlineData("not-begin-with", "Beta")]
    [InlineData("ends-with", "a")]
    [InlineData("not-end-with", "a")]
    public void AValueEndingInCarriageReturnLineFeed_IsRefused_NotMatched(string op, string value)
    {
        var error = Assert.Throws<NotSupportedException>(() => Keys(Dataverse("Beta", "Beta\r\n", "Gamma"), op, value));
        Assert.Contains("not observed", error.Message, StringComparison.Ordinal);
        Assert.Contains("U+000D", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The value forms are anchored the same way: a trailing line feed is not a whole number, decimal or date.</summary>
    [Theory]
    [InlineData("k", "5&#10;")]
    [InlineData("k", "-5&#10;")]
    public void AConditionValueEndingInALineFeed_IsRefused_NotRead(string attribute, string value)
    {
        var dataverse = Dataverse("Beta");
        var error = Record.Exception(() => dataverse.RetrieveMultiple(
            $"<fetch><entity name=\"t\"><attribute name=\"k\" /><filter><condition attribute=\"{attribute}\" operator=\"eq\" value=\"{value}\" /></filter></entity></fetch>"));
        Assert.True(error is NotSupportedException { Message: var m } && m.Contains("not observed", StringComparison.Ordinal),
            $"expected a \"not observed\" refusal, got {(error is null ? "rows" : error.GetType().Name + ": " + error.Message)}");
    }
}
