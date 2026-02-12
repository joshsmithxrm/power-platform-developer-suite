using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class QueryHintParserTests
{
    private readonly QueryParser _parser = new();

    [Fact]
    public void Parse_NoHints_ReturnsEmpty()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("SELECT name FROM account"));

        Assert.Null(overrides.UseTdsEndpoint);
        Assert.Null(overrides.DmlBatchSize);
    }

    [Fact]
    public void Parse_MaxDop_SetsValue()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("SELECT name FROM account OPTION (MAXDOP 4)"));

        Assert.Equal(4, overrides.MaxParallelism);
    }

    [Fact]
    public void Parse_CommentHint_UseTds_SetsFlag()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:USE_TDS\nSELECT name FROM account"));

        Assert.True(overrides.UseTdsEndpoint);
    }

    [Fact]
    public void Parse_CommentHint_BatchSize_SetsValue()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:BATCH_SIZE 50\nDELETE FROM account WHERE x = 1"));

        Assert.Equal(50, overrides.DmlBatchSize);
    }

    [Fact]
    public void Parse_CommentHint_MaxDop_SetsValue()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:MAXDOP 4\nDELETE FROM account WHERE x = 1"));

        Assert.Equal(4, overrides.MaxParallelism);
    }

    [Fact]
    public void Parse_CommentHint_MultipleHints_SetsAll()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:BATCH_SIZE 50\n-- ppds:MAXDOP 4\nDELETE FROM account WHERE x = 1"));

        Assert.Equal(50, overrides.DmlBatchSize);
        Assert.Equal(4, overrides.MaxParallelism);
    }

    [Fact]
    public void Parse_CommentHint_BypassPlugins_SetsFlag()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:BYPASS_PLUGINS\nUPDATE account SET name = 'x' WHERE accountid = '123'"));

        Assert.True(overrides.BypassPlugins);
    }

    [Fact]
    public void Parse_CommentHint_BypassFlows_SetsFlag()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- ppds:BYPASS_FLOWS\nUPDATE account SET name = 'x' WHERE accountid = '123'"));

        Assert.True(overrides.BypassFlows);
    }

    [Fact]
    public void Parse_NonSelectStatement_ReturnsEmpty()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("INSERT INTO account (name) VALUES ('test')"));

        Assert.Null(overrides.UseTdsEndpoint);
        Assert.Null(overrides.DmlBatchSize);
    }

    [Fact]
    public void Parse_RegularComment_Ignored()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("-- This is a regular comment\nSELECT name FROM account"));

        Assert.Null(overrides.UseTdsEndpoint);
        Assert.Null(overrides.DmlBatchSize);
        Assert.Null(overrides.MaxParallelism);
    }
}
