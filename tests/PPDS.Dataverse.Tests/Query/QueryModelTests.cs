using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Dataverse.Tests.Query;

[Trait("Category", "Unit")]
public class QueryResultTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var columns = new List<QueryColumn>
        {
            new() { LogicalName = "name" }
        };
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Contoso")
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = columns,
            Records = records,
            Count = 1
        };

        Assert.Equal("account", result.EntityLogicalName);
        Assert.Single(result.Columns);
        Assert.Single(result.Records);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = Array.Empty<QueryColumn>(),
            Records = Array.Empty<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        Assert.Null(result.TotalCount);
        Assert.False(result.MoreRecords);
        Assert.Null(result.PagingCookie);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(0, result.ExecutionTimeMs);
        Assert.Null(result.ExecutedFetchXml);
        Assert.False(result.IsAggregate);
    }

    [Fact]
    public void OptionalProperties_CanBeSet()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "contact",
            Columns = Array.Empty<QueryColumn>(),
            Records = Array.Empty<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 50,
            TotalCount = 200,
            MoreRecords = true,
            PagingCookie = "<cookie page=\"1\" />",
            PageNumber = 2,
            ExecutionTimeMs = 123,
            ExecutedFetchXml = "<fetch><entity name='contact'/></fetch>",
            IsAggregate = true
        };

        Assert.Equal(200, result.TotalCount);
        Assert.True(result.MoreRecords);
        Assert.Equal("<cookie page=\"1\" />", result.PagingCookie);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(123, result.ExecutionTimeMs);
        Assert.Equal("<fetch><entity name='contact'/></fetch>", result.ExecutedFetchXml);
        Assert.True(result.IsAggregate);
    }

    [Fact]
    public void Empty_ReturnsResultWithNoRecords()
    {
        var result = QueryResult.Empty("account");

        Assert.Equal("account", result.EntityLogicalName);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Records);
        Assert.Equal(0, result.Count);
        Assert.False(result.MoreRecords);
    }

    [Fact]
    public void Empty_DefaultProperties_AreCorrect()
    {
        var result = QueryResult.Empty("contact");

        Assert.Null(result.TotalCount);
        Assert.Null(result.PagingCookie);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(0, result.ExecutionTimeMs);
        Assert.False(result.IsAggregate);
    }
}

[Trait("Category", "Unit")]
public class QueryColumnTests
{
    [Fact]
    public void Constructor_WithRequiredLogicalName_SetsValue()
    {
        var column = new QueryColumn { LogicalName = "name" };

        Assert.Equal("name", column.LogicalName);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var column = new QueryColumn { LogicalName = "name" };

        Assert.Null(column.Alias);
        Assert.Null(column.DisplayName);
        Assert.Equal(QueryColumnType.Unknown, column.DataType);
        Assert.Null(column.LinkedEntityAlias);
        Assert.Null(column.LinkedEntityName);
        Assert.False(column.IsAggregate);
        Assert.Null(column.AggregateFunction);
    }

    [Fact]
    public void EffectiveName_ReturnsLogicalName_WhenAliasIsNull()
    {
        var column = new QueryColumn { LogicalName = "name" };

        Assert.Equal("name", column.EffectiveName);
    }

    [Fact]
    public void EffectiveName_ReturnsAlias_WhenAliasIsSet()
    {
        var column = new QueryColumn
        {
            LogicalName = "name",
            Alias = "account_name"
        };

        Assert.Equal("account_name", column.EffectiveName);
    }

    [Fact]
    public void QualifiedName_ReturnsLogicalName_WhenLinkedEntityAliasIsNull()
    {
        var column = new QueryColumn { LogicalName = "name" };

        Assert.Equal("name", column.QualifiedName);
    }

    [Fact]
    public void QualifiedName_ReturnsPrefixedName_WhenLinkedEntityAliasIsSet()
    {
        var column = new QueryColumn
        {
            LogicalName = "fullname",
            LinkedEntityAlias = "contact"
        };

        Assert.Equal("contact.fullname", column.QualifiedName);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var column = new QueryColumn
        {
            LogicalName = "total",
            Alias = "sum_total",
            DisplayName = "Total Amount",
            DataType = QueryColumnType.Money,
            LinkedEntityAlias = "lineitem",
            LinkedEntityName = "salesorderdetail",
            IsAggregate = true,
            AggregateFunction = "sum"
        };

        Assert.Equal("total", column.LogicalName);
        Assert.Equal("sum_total", column.Alias);
        Assert.Equal("Total Amount", column.DisplayName);
        Assert.Equal(QueryColumnType.Money, column.DataType);
        Assert.Equal("lineitem", column.LinkedEntityAlias);
        Assert.Equal("salesorderdetail", column.LinkedEntityName);
        Assert.True(column.IsAggregate);
        Assert.Equal("sum", column.AggregateFunction);
    }
}

[Trait("Category", "Unit")]
public class QueryValueTests
{
    [Fact]
    public void Simple_CreatesValueWithNoFormatting()
    {
        var value = QueryValue.Simple("hello");

        Assert.Equal("hello", value.Value);
        Assert.Null(value.FormattedValue);
        Assert.Null(value.LookupEntityType);
        Assert.Null(value.LookupEntityId);
    }

    [Fact]
    public void Simple_WithNull_CreatesNullValue()
    {
        var value = QueryValue.Simple(null);

        Assert.Null(value.Value);
    }

    [Fact]
    public void Simple_WithInt_CreatesIntValue()
    {
        var value = QueryValue.Simple(42);

        Assert.Equal(42, value.Value);
    }

    [Fact]
    public void WithFormatting_CreatesValueWithFormattedText()
    {
        var value = QueryValue.WithFormatting(100.50m, "$100.50");

        Assert.Equal(100.50m, value.Value);
        Assert.Equal("$100.50", value.FormattedValue);
    }

    [Fact]
    public void WithFormatting_NullFormatted_SetsFormattedToNull()
    {
        var value = QueryValue.WithFormatting("test", null);

        Assert.Equal("test", value.Value);
        Assert.Null(value.FormattedValue);
    }

    [Fact]
    public void Lookup_CreatesLookupWithAllProperties()
    {
        var id = Guid.NewGuid();

        var value = QueryValue.Lookup(id, "account", "Contoso");

        Assert.Equal(id, value.Value);
        Assert.Equal("Contoso", value.FormattedValue);
        Assert.Equal("account", value.LookupEntityType);
        Assert.Equal(id, value.LookupEntityId);
    }

    [Fact]
    public void Lookup_WithNullDisplayName_SetsFormattedToNull()
    {
        var id = Guid.NewGuid();

        var value = QueryValue.Lookup(id, "contact", null);

        Assert.Null(value.FormattedValue);
        Assert.Equal("contact", value.LookupEntityType);
        Assert.Equal(id, value.LookupEntityId);
    }

    [Fact]
    public void Null_CreatesValueWithNullValue()
    {
        var value = QueryValue.Null;

        Assert.Null(value.Value);
        Assert.Null(value.FormattedValue);
        Assert.Null(value.LookupEntityType);
        Assert.Null(value.LookupEntityId);
    }

    [Fact]
    public void IsLookup_ReturnsTrue_WhenLookupEntityIdHasValue()
    {
        var value = QueryValue.Lookup(Guid.NewGuid(), "account", "Contoso");

        Assert.True(value.IsLookup);
    }

    [Fact]
    public void IsLookup_ReturnsFalse_WhenLookupEntityIdIsNull()
    {
        var value = QueryValue.Simple("test");

        Assert.False(value.IsLookup);
    }

    [Fact]
    public void IsOptionSet_ReturnsTrue_WhenValueIsIntAndFormattedValueIsSet()
    {
        var value = QueryValue.WithFormatting(3, "Active");

        Assert.True(value.IsOptionSet);
    }

    [Fact]
    public void IsOptionSet_ReturnsFalse_WhenValueIsIntButNoFormattedValue()
    {
        var value = QueryValue.Simple(3);

        Assert.False(value.IsOptionSet);
    }

    [Fact]
    public void IsOptionSet_ReturnsFalse_WhenValueIsNotInt()
    {
        var value = QueryValue.WithFormatting("text", "Text Label");

        Assert.False(value.IsOptionSet);
    }

    [Fact]
    public void IsBoolean_ReturnsTrue_WhenValueIsBool()
    {
        var value = QueryValue.Simple(true);

        Assert.True(value.IsBoolean);
    }

    [Fact]
    public void IsBoolean_ReturnsFalse_WhenValueIsNotBool()
    {
        var value = QueryValue.Simple("true");

        Assert.False(value.IsBoolean);
    }

    [Fact]
    public void IsBoolean_ReturnsFalse_WhenValueIsNull()
    {
        var value = QueryValue.Null;

        Assert.False(value.IsBoolean);
    }

    [Fact]
    public void HasFormattedValue_ReturnsTrue_WhenFormattedValueIsSet()
    {
        var value = QueryValue.WithFormatting(1, "Yes");

        Assert.True(value.HasFormattedValue);
    }

    [Fact]
    public void HasFormattedValue_ReturnsFalse_WhenFormattedValueIsNull()
    {
        var value = QueryValue.Simple("test");

        Assert.False(value.HasFormattedValue);
    }

    [Fact]
    public void HasFormattedValue_ReturnsFalse_WhenFormattedValueIsEmpty()
    {
        var value = QueryValue.WithFormatting("test", "");

        Assert.False(value.HasFormattedValue);
    }
}

[Trait("Category", "Unit")]
public class QueryColumnTypeTests
{
    [Fact]
    public void Unknown_IsDefaultValue()
    {
        Assert.Equal(0, (int)QueryColumnType.Unknown);
    }

    [Fact]
    public void DefaultEnum_IsUnknown()
    {
        QueryColumnType defaultType = default;

        Assert.Equal(QueryColumnType.Unknown, defaultType);
    }

    [Theory]
    [InlineData(QueryColumnType.Unknown)]
    [InlineData(QueryColumnType.String)]
    [InlineData(QueryColumnType.Integer)]
    [InlineData(QueryColumnType.BigInt)]
    [InlineData(QueryColumnType.Decimal)]
    [InlineData(QueryColumnType.Double)]
    [InlineData(QueryColumnType.Money)]
    [InlineData(QueryColumnType.Boolean)]
    [InlineData(QueryColumnType.DateTime)]
    [InlineData(QueryColumnType.Guid)]
    [InlineData(QueryColumnType.Lookup)]
    [InlineData(QueryColumnType.OptionSet)]
    [InlineData(QueryColumnType.MultiSelectOptionSet)]
    [InlineData(QueryColumnType.Image)]
    [InlineData(QueryColumnType.Memo)]
    [InlineData(QueryColumnType.AliasedValue)]
    public void EnumValue_IsDefined(QueryColumnType type)
    {
        Assert.True(Enum.IsDefined(typeof(QueryColumnType), type));
    }

    [Fact]
    public void AllExpectedValues_Exist()
    {
        var values = Enum.GetValues<QueryColumnType>();

        Assert.Equal(16, values.Length);
    }
}

[Trait("Category", "Unit")]
public class QueryExecutionOptionsTests
{
    [Fact]
    public void DefaultValues_AreFalse()
    {
        var options = new QueryExecutionOptions();

        Assert.False(options.BypassPlugins);
        Assert.False(options.BypassFlows);
    }

    [Fact]
    public void BypassPlugins_CanBeSetToTrue()
    {
        var options = new QueryExecutionOptions { BypassPlugins = true };

        Assert.True(options.BypassPlugins);
        Assert.False(options.BypassFlows);
    }

    [Fact]
    public void BypassFlows_CanBeSetToTrue()
    {
        var options = new QueryExecutionOptions { BypassFlows = true };

        Assert.False(options.BypassPlugins);
        Assert.True(options.BypassFlows);
    }

    [Fact]
    public void BothProperties_CanBeSetToTrue()
    {
        var options = new QueryExecutionOptions
        {
            BypassPlugins = true,
            BypassFlows = true
        };

        Assert.True(options.BypassPlugins);
        Assert.True(options.BypassFlows);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = false };
        var b = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = false };

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = false };
        var b = new QueryExecutionOptions { BypassPlugins = false, BypassFlows = true };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DefaultValues_AreEqual()
    {
        var a = new QueryExecutionOptions();
        var b = new QueryExecutionOptions();

        Assert.Equal(a, b);
    }

    [Fact]
    public void GetHashCode_SameValues_AreSame()
    {
        var a = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = true };
        var b = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = true };

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
