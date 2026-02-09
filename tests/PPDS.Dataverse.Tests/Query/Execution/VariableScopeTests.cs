using System;
using PPDS.Dataverse.Query.Execution;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "TuiUnit")]
public class VariableScopeTests
{
    [Fact]
    public void DeclareAndGet_ReturnsInitialValue()
    {
        var scope = new VariableScope();
        scope.Declare("@threshold", "MONEY", 1000000m);

        var value = scope.Get("@threshold");
        Assert.Equal(1000000m, value);
    }

    [Fact]
    public void DeclareWithoutValue_ReturnsNull()
    {
        var scope = new VariableScope();
        scope.Declare("@name", "NVARCHAR(100)");

        var value = scope.Get("@name");
        Assert.Null(value);
    }

    [Fact]
    public void SetVariable_UpdatesValue()
    {
        var scope = new VariableScope();
        scope.Declare("@count", "INT", 0);
        scope.Set("@count", 42);

        Assert.Equal(42, scope.Get("@count"));
    }

    [Fact]
    public void GetUndeclared_Throws()
    {
        var scope = new VariableScope();

        var ex = Assert.Throws<QueryExecutionException>(() => scope.Get("@missing"));
        Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
        Assert.Contains("@missing", ex.Message);
        Assert.Contains("not been declared", ex.Message);
    }

    [Fact]
    public void SetUndeclared_Throws()
    {
        var scope = new VariableScope();

        var ex = Assert.Throws<QueryExecutionException>(() => scope.Set("@missing", 42));
        Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
        Assert.Contains("@missing", ex.Message);
        Assert.Contains("not been declared", ex.Message);
    }

    [Fact]
    public void CaseInsensitiveVariableNames()
    {
        var scope = new VariableScope();
        scope.Declare("@MyVar", "INT", 100);

        // Get with different casing
        Assert.Equal(100, scope.Get("@myvar"));
        Assert.Equal(100, scope.Get("@MYVAR"));
        Assert.Equal(100, scope.Get("@MyVar"));
    }

    [Fact]
    public void IsDeclared_ReturnsTrueForDeclared()
    {
        var scope = new VariableScope();
        scope.Declare("@x", "INT");

        Assert.True(scope.IsDeclared("@x"));
        Assert.False(scope.IsDeclared("@y"));
    }

    [Fact]
    public void IsDeclared_CaseInsensitive()
    {
        var scope = new VariableScope();
        scope.Declare("@Threshold", "MONEY");

        Assert.True(scope.IsDeclared("@threshold"));
        Assert.True(scope.IsDeclared("@THRESHOLD"));
    }

    [Fact]
    public void DeclareWithoutAtPrefix_Throws()
    {
        var scope = new VariableScope();

        var ex = Assert.Throws<ArgumentException>(() => scope.Declare("badname", "INT"));
        Assert.Contains("must start with @", ex.Message);
    }

    [Fact]
    public void SetVariable_ToNull()
    {
        var scope = new VariableScope();
        scope.Declare("@val", "NVARCHAR(100)", "hello");
        scope.Set("@val", null);

        Assert.Null(scope.Get("@val"));
    }

    [Fact]
    public void Variables_ReturnsAllDeclared()
    {
        var scope = new VariableScope();
        scope.Declare("@a", "INT", 1);
        scope.Declare("@b", "NVARCHAR(50)", "test");

        Assert.Equal(2, scope.Variables.Count);
    }

    [Fact]
    public void VariableInfo_RecordEquality()
    {
        var info1 = new VariableInfo("@x", "INT", 42);
        var info2 = new VariableInfo("@x", "INT", 42);

        Assert.Equal(info1, info2);
    }

    [Fact]
    public void VariableInfo_WithExpression()
    {
        var info = new VariableInfo("@x", "INT", 10);
        var updated = info with { Value = 20 };

        Assert.Equal(10, info.Value);
        Assert.Equal(20, updated.Value);
    }
}
