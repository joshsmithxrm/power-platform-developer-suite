using System;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using Xunit;

namespace PPDS.Dataverse.Tests.Query;

/// <summary>
/// Unit tests for <see cref="DmlValueCoercer"/> — the helper that turns primitive CLR
/// values from SQL DML into Dataverse SDK types. Regression coverage for issue #787
/// ("ppds query sql DML does not coerce lookup or choice values into Dataverse types").
/// </summary>
[Trait("Category", "TuiUnit")]
public class DmlValueCoercerTests
{
    private static AttributeMetadataDto Attr(
        string type,
        string name = "col",
        System.Collections.Generic.List<string>? targets = null,
        string? attributeTypeName = null)
        => new()
        {
            LogicalName = name,
            DisplayName = name,
            SchemaName = name,
            AttributeType = type,
            Targets = targets,
            AttributeTypeName = attributeTypeName
        };

    [Fact]
    public void Coerce_NullValue_ReturnsNull()
    {
        Assert.Null(DmlValueCoercer.Coerce(null, Attr("String")));
    }

    [Fact]
    public void Coerce_NullMetadata_ReturnsValueUnchanged()
    {
        Assert.Equal(42, DmlValueCoercer.Coerce(42, null));
    }

    [Fact]
    public void Coerce_PicklistInt_ReturnsOptionSetValue()
    {
        var result = DmlValueCoercer.Coerce(100000000, Attr("Picklist"));
        var option = Assert.IsType<OptionSetValue>(result);
        Assert.Equal(100000000, option.Value);
    }

    [Fact]
    public void Coerce_StateStatusNumericString_ReturnsOptionSetValue()
    {
        var state = DmlValueCoercer.Coerce("1", Attr("State"));
        var status = DmlValueCoercer.Coerce(2, Attr("Status"));

        Assert.IsType<OptionSetValue>(state);
        Assert.Equal(1, ((OptionSetValue)state!).Value);
        Assert.IsType<OptionSetValue>(status);
        Assert.Equal(2, ((OptionSetValue)status!).Value);
    }

    [Fact]
    public void Coerce_PicklistPassthrough_WhenAlreadyOptionSetValue()
    {
        var existing = new OptionSetValue(7);
        var result = DmlValueCoercer.Coerce(existing, Attr("Picklist"));
        Assert.Same(existing, result);
    }

    [Fact]
    public void Coerce_LookupGuidString_ReturnsEntityReference()
    {
        var id = Guid.NewGuid();
        var result = DmlValueCoercer.Coerce(
            id.ToString(),
            Attr("Lookup", "hsl_clinic", targets: new() { "hsl_clinic" }));

        var reference = Assert.IsType<EntityReference>(result);
        Assert.Equal("hsl_clinic", reference.LogicalName);
        Assert.Equal(id, reference.Id);
    }

    [Fact]
    public void Coerce_LookupGuidValue_ReturnsEntityReference()
    {
        var id = Guid.NewGuid();
        var result = DmlValueCoercer.Coerce(
            id,
            Attr("Lookup", "primarycontactid", targets: new() { "contact" }));

        var reference = Assert.IsType<EntityReference>(result);
        Assert.Equal("contact", reference.LogicalName);
        Assert.Equal(id, reference.Id);
    }

    [Fact]
    public void Coerce_LookupPolymorphic_ThrowsQueryExecutionException()
    {
        var attr = Attr("Customer", "customerid", targets: new() { "account", "contact" });
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce(Guid.NewGuid().ToString(), attr));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("polymorphic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coerce_LookupNoTargets_ThrowsQueryExecutionException()
    {
        var attr = Attr("Lookup", "orphan");
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce(Guid.NewGuid().ToString(), attr));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Coerce_LookupBadGuid_ThrowsQueryExecutionException()
    {
        var attr = Attr("Lookup", "hsl_clinic", targets: new() { "hsl_clinic" });
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("not-a-guid", attr));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Coerce_MultiSelectPicklist_ThrowsUnsupported()
    {
        var attr = Attr("Virtual", "hsl_tags", attributeTypeName: "MultiSelectPicklistType");
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("1,2,3", attr));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Multi-select", ex.Message);
    }

    [Fact]
    public void Coerce_VirtualNonMultiSelect_PassesThrough()
    {
        // Virtual attributes that are not MultiSelectPicklist (e.g., File, Image) pass
        // through unchanged — the coercer does not know how to serialize them, and
        // today's behavior of letting the SDK raise a type error is preserved.
        var attr = Attr("Virtual", "hsl_file", attributeTypeName: "FileType");
        Assert.Equal("some-bytes", DmlValueCoercer.Coerce("some-bytes", attr));
    }

    [Fact]
    public void Coerce_DateTimeWithOffset_PreservesUtcKind()
    {
        var result = DmlValueCoercer.Coerce("2026-04-19T12:00:00Z", Attr("DateTime"));
        var dt = Assert.IsType<DateTime>(result);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Fact]
    public void Coerce_DateTimeUnqualified_ParsesAsUnspecified()
    {
        // Bare "yyyy-MM-dd HH:mm:ss" stays Unspecified so the Dataverse SDK serializes
        // it per the column's DateTimeBehavior (UserLocal / TimeZoneIndependent / DateOnly).
        var result = DmlValueCoercer.Coerce("2026-04-19 09:00:00", Attr("DateTime"));
        var dt = Assert.IsType<DateTime>(result);
        Assert.Equal(DateTimeKind.Unspecified, dt.Kind);
        Assert.Equal(9, dt.Hour);
    }

    [Fact]
    public void Coerce_MoneyDecimal_ReturnsMoney()
    {
        var result = DmlValueCoercer.Coerce(1234.56m, Attr("Money"));
        var money = Assert.IsType<Money>(result);
        Assert.Equal(1234.56m, money.Value);
    }

    [Fact]
    public void Coerce_MoneyInt_ReturnsMoney()
    {
        var result = DmlValueCoercer.Coerce(500, Attr("Money"));
        var money = Assert.IsType<Money>(result);
        Assert.Equal(500m, money.Value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Coerce_BooleanFromString_ReturnsBool(string input, bool expected)
    {
        var result = DmlValueCoercer.Coerce(input, Attr("Boolean"));
        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Fact]
    public void Coerce_UniqueidentifierFromString_ReturnsGuid()
    {
        var id = Guid.NewGuid();
        var result = DmlValueCoercer.Coerce(id.ToString(), Attr("Uniqueidentifier"));
        Assert.Equal(id, Assert.IsType<Guid>(result));
    }

    [Fact]
    public void Coerce_BigIntFromInt_ReturnsLong()
    {
        var result = DmlValueCoercer.Coerce(42, Attr("BigInt"));
        Assert.Equal(42L, Assert.IsType<long>(result));
    }

    [Fact]
    public void Coerce_IntegerFromDecimal_ReturnsInt()
    {
        var result = DmlValueCoercer.Coerce(7m, Attr("Integer"));
        Assert.Equal(7, Assert.IsType<int>(result));
    }

    [Fact]
    public void Coerce_DecimalPassthrough()
    {
        var result = DmlValueCoercer.Coerce(3.14m, Attr("Decimal"));
        Assert.Equal(3.14m, Assert.IsType<decimal>(result));
    }

    [Fact]
    public void Coerce_DoubleFromDecimal_ReturnsDouble()
    {
        var result = DmlValueCoercer.Coerce(2.5m, Attr("Double"));
        Assert.Equal(2.5, Assert.IsType<double>(result));
    }

    [Fact]
    public void Coerce_DateTimeFromString_ReturnsDateTime()
    {
        var result = DmlValueCoercer.Coerce("2026-04-19T12:00:00Z", Attr("DateTime"));
        var dt = Assert.IsType<DateTime>(result);
        Assert.Equal(2026, dt.Year);
        Assert.Equal(4, dt.Month);
    }

    [Fact]
    public void Coerce_StringPassthrough()
    {
        var result = DmlValueCoercer.Coerce("hello", Attr("String"));
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Coerce_UnknownType_PassesThrough()
    {
        var result = DmlValueCoercer.Coerce(42, Attr("SomeFutureType"));
        Assert.Equal(42, result);
    }

    // -----------------------------------------------------------------------
    // Structured-error coverage for Gemini findings on PR #827 (DmlValueCoercer
    // lines 155, 168, 181, 201, 222, 233). Verifies that bad inputs surface as
    // QueryExecutionException(TypeMismatch) instead of raw Format/Overflow
    // exceptions from Convert.To* / Parse.
    // -----------------------------------------------------------------------

    [Fact]
    public void Coerce_BooleanFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("yes", Attr("Boolean")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Boolean", ex.Message);
    }

    [Fact]
    public void Coerce_BooleanFromUnsupportedType_ThrowsQueryExecutionException()
    {
        // DateTime is not in the Boolean coercion switch — must throw structured.
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce(DateTime.UtcNow, Attr("Boolean")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Coerce_BigIntFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("not-a-number", Attr("BigInt")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Int64", ex.Message);
    }

    [Fact]
    public void Coerce_DoubleFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("nan-not-a-num", Attr("Double")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Double", ex.Message);
    }

    [Fact]
    public void Coerce_DateTimeFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("not-a-date", Attr("DateTime")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("DateTime", ex.Message);
    }

    [Fact]
    public void Coerce_IntegerFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("xyz", Attr("Integer")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void Coerce_IntegerFromOverflowLong_ThrowsQueryExecutionException()
    {
        // long > int.MaxValue must surface a structured error rather than a
        // checked-conversion OverflowException from Convert.ToInt32.
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce((long)int.MaxValue + 1L, Attr("Integer")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Coerce_DecimalFromBadString_ThrowsQueryExecutionException()
    {
        var ex = Assert.Throws<QueryExecutionException>(
            () => DmlValueCoercer.Coerce("not-a-decimal", Attr("Decimal")));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
        Assert.Contains("Decimal", ex.Message);
    }

    [Fact]
    public void Coerce_BigIntFromInvariantString_Parses()
    {
        // Confirms the explicit InvariantCulture-bound TryParse path works for the
        // common ScriptDom string-literal case.
        var result = DmlValueCoercer.Coerce("9223372036854775807", Attr("BigInt"));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(result));
    }

    [Fact]
    public void Coerce_DateTimeFromDateOnlyString_ReturnsDateTime()
    {
        // Regression for #866: SQL date literal '2026-05-20' (no time component)
        // must coerce to DateTime, not pass through as a raw string.
        var result = DmlValueCoercer.Coerce("2026-05-20", Attr("DateTime"));
        var dt = Assert.IsType<DateTime>(result);
        Assert.Equal(2026, dt.Year);
        Assert.Equal(5, dt.Month);
        Assert.Equal(20, dt.Day);
        Assert.Equal(DateTimeKind.Unspecified, dt.Kind);
    }
}
