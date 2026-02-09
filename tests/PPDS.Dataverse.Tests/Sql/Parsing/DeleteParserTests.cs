using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class DeleteParserTests
{
    [Fact]
    public void ParsesSimpleDelete()
    {
        var sql = "DELETE FROM account WHERE statecode = 2";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.Equal("account", delete.TargetTable.TableName);
        Assert.NotNull(delete.Where);
        Assert.Null(delete.FromTable);
        Assert.Null(delete.Joins);
    }

    [Fact]
    public void ParsesDeleteWithAndCondition()
    {
        var sql = "DELETE FROM account WHERE statecode = 2 AND revenue < 100";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.NotNull(delete.Where);
        Assert.IsType<SqlLogicalCondition>(delete.Where);
    }

    [Fact]
    public void ParseError_DeleteWithoutWhere()
    {
        var sql = "DELETE FROM account";
        var ex = Assert.Throws<SqlParseException>(() => SqlParser.ParseSql(sql));
        Assert.Contains("DELETE without WHERE is not allowed", ex.Message);
        Assert.Contains("ppds truncate", ex.Message);
    }

    [Fact]
    public void DeleteIsCaseInsensitive()
    {
        var sql = "delete from account where statecode = 2";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.Equal("account", delete.TargetTable.TableName);
    }

    [Fact]
    public void DeleteHasCorrectSourcePosition()
    {
        var sql = "DELETE FROM account WHERE statecode = 2";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.Equal(0, delete.SourcePosition);
    }

    [Fact]
    public void ParsesDeleteWithFromJoin()
    {
        var sql = "DELETE FROM a FROM account a JOIN contact c ON a.accountid = c.parentcustomerid WHERE c.statecode = 1";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.Equal("a", delete.TargetTable.TableName);
        Assert.NotNull(delete.FromTable);
        Assert.Equal("account", delete.FromTable!.TableName);
        Assert.NotNull(delete.Joins);
        Assert.Single(delete.Joins!);
        Assert.NotNull(delete.Where);
    }

    [Fact]
    public void ParsesDeleteWithInClause()
    {
        var sql = "DELETE FROM account WHERE statecode IN (2, 3)";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.NotNull(delete.Where);
        Assert.IsType<SqlInCondition>(delete.Where);
    }

    [Fact]
    public void ParsesDeleteWithLikeCondition()
    {
        var sql = "DELETE FROM account WHERE name LIKE 'Test%'";
        var statement = SqlParser.ParseSql(sql);

        var delete = Assert.IsType<SqlDeleteStatement>(statement);
        Assert.NotNull(delete.Where);
        Assert.IsType<SqlLikeCondition>(delete.Where);
    }
}
