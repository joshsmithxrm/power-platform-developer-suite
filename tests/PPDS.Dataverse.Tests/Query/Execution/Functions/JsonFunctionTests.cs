using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "Unit")]
public class JsonFunctionTests
{
    private readonly ExpressionEvaluator _eval = new();

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private static SqlFunctionExpression Fn(string name, params ISqlExpression[] args)
    {
        return new SqlFunctionExpression(name, args);
    }

    private static SqlLiteralExpression Str(string value) =>
        new(SqlLiteral.String(value));

    private static SqlLiteralExpression Num(string value) =>
        new(SqlLiteral.Number(value));

    private static SqlLiteralExpression Null() =>
        new(SqlLiteral.Null());

    private const string SampleJson = """{"name":"John","age":30,"address":{"city":"Seattle","zip":"98101"},"tags":["dev","admin"]}""";

    #region JSON_VALUE

    [Fact]
    public void JsonValue_SimpleProperty()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.name")), EmptyRow);
        Assert.Equal("John", result);
    }

    [Fact]
    public void JsonValue_NumericProperty()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.age")), EmptyRow);
        Assert.Equal("30", result);
    }

    [Fact]
    public void JsonValue_NestedProperty()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.address.city")), EmptyRow);
        Assert.Equal("Seattle", result);
    }

    [Fact]
    public void JsonValue_ArrayElement()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.tags[0]")), EmptyRow);
        Assert.Equal("dev", result);
    }

    [Fact]
    public void JsonValue_ArraySecondElement()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.tags[1]")), EmptyRow);
        Assert.Equal("admin", result);
    }

    [Fact]
    public void JsonValue_Object_ReturnsNull()
    {
        // JSON_VALUE should not return objects
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.address")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonValue_NonexistentPath_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Str("$.nonexistent")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonValue_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Null(), Str("$.name")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonValue_NullPath_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(SampleJson), Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonValue_InvalidJson_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str("not json"), Str("$.name")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonValue_BooleanProperty()
    {
        var json = """{"active":true}""";
        var result = _eval.Evaluate(Fn("JSON_VALUE", Str(json), Str("$.active")), EmptyRow);
        Assert.Equal("true", result);
    }

    #endregion

    #region JSON_QUERY

    [Fact]
    public void JsonQuery_Object()
    {
        var result = _eval.Evaluate(Fn("JSON_QUERY", Str(SampleJson), Str("$.address")), EmptyRow);
        Assert.NotNull(result);
        Assert.Contains("Seattle", (string)result!);
    }

    [Fact]
    public void JsonQuery_Array()
    {
        var result = _eval.Evaluate(Fn("JSON_QUERY", Str(SampleJson), Str("$.tags")), EmptyRow);
        Assert.NotNull(result);
        Assert.Contains("dev", (string)result!);
    }

    [Fact]
    public void JsonQuery_Scalar_ReturnsNull()
    {
        // JSON_QUERY should not return scalar values
        var result = _eval.Evaluate(Fn("JSON_QUERY", Str(SampleJson), Str("$.name")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonQuery_RootNoPath()
    {
        // JSON_QUERY with 1 arg returns root if object/array
        var result = _eval.Evaluate(Fn("JSON_QUERY", Str(SampleJson)), EmptyRow);
        Assert.NotNull(result);
        Assert.Contains("John", (string)result!);
    }

    [Fact]
    public void JsonQuery_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_QUERY", Null(), Str("$.address")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region JSON_PATH_EXISTS

    [Fact]
    public void JsonPathExists_Exists()
    {
        var result = _eval.Evaluate(Fn("JSON_PATH_EXISTS", Str(SampleJson), Str("$.name")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void JsonPathExists_NotExists()
    {
        var result = _eval.Evaluate(Fn("JSON_PATH_EXISTS", Str(SampleJson), Str("$.foo")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void JsonPathExists_NestedExists()
    {
        var result = _eval.Evaluate(Fn("JSON_PATH_EXISTS", Str(SampleJson), Str("$.address.city")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void JsonPathExists_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("JSON_PATH_EXISTS", Null(), Str("$.name")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonPathExists_InvalidJson_ReturnsZero()
    {
        var result = _eval.Evaluate(Fn("JSON_PATH_EXISTS", Str("not json"), Str("$.name")), EmptyRow);
        Assert.Equal(0, result);
    }

    #endregion

    #region JSON_MODIFY

    [Fact]
    public void JsonModify_UpdateExistingProperty()
    {
        var result = _eval.Evaluate(
            Fn("JSON_MODIFY", Str(SampleJson), Str("$.name"), Str("Jane")),
            EmptyRow);
        Assert.NotNull(result);
        var resultStr = (string)result!;
        Assert.Contains("Jane", resultStr);
        Assert.DoesNotContain("\"John\"", resultStr);
    }

    [Fact]
    public void JsonModify_SetToNull()
    {
        var result = _eval.Evaluate(
            Fn("JSON_MODIFY", Str(SampleJson), Str("$.name"), Null()),
            EmptyRow);
        Assert.NotNull(result);
        Assert.Contains("null", (string)result!);
    }

    [Fact]
    public void JsonModify_AddNewProperty()
    {
        var json = """{"name":"John"}""";
        var result = _eval.Evaluate(
            Fn("JSON_MODIFY", Str(json), Str("$.email"), Str("john@test.com")),
            EmptyRow);
        Assert.NotNull(result);
        Assert.Contains("email", (string)result!);
        Assert.Contains("john@test.com", (string)result!);
    }

    [Fact]
    public void JsonModify_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("JSON_MODIFY", Null(), Str("$.name"), Str("Jane")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void JsonModify_NullPath_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("JSON_MODIFY", Str(SampleJson), Null(), Str("Jane")),
            EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region ISJSON

    [Fact]
    public void IsJson_ValidObject()
    {
        var result = _eval.Evaluate(Fn("ISJSON", Str(SampleJson)), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void IsJson_ValidArray()
    {
        var result = _eval.Evaluate(Fn("ISJSON", Str("[1,2,3]")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void IsJson_InvalidJson()
    {
        var result = _eval.Evaluate(Fn("ISJSON", Str("not json")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void IsJson_EmptyString()
    {
        var result = _eval.Evaluate(Fn("ISJSON", Str("")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void IsJson_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("ISJSON", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void IsJson_SimpleString()
    {
        // A simple quoted string is valid JSON
        var result = _eval.Evaluate(Fn("ISJSON", Str("\"hello\"")), EmptyRow);
        Assert.Equal(1, result);
    }

    #endregion

    #region Registry

    [Fact]
    public void AllJsonFunctionsRegistered()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("JSON_VALUE"));
        Assert.True(registry.IsRegistered("JSON_QUERY"));
        Assert.True(registry.IsRegistered("JSON_PATH_EXISTS"));
        Assert.True(registry.IsRegistered("JSON_MODIFY"));
        Assert.True(registry.IsRegistered("ISJSON"));
    }

    #endregion
}
