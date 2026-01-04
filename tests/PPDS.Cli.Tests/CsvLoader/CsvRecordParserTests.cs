using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class CsvRecordParserTests
{
    private readonly CsvRecordParser _parser = new();

    #region String Tests

    [Fact]
    public void CoerceValue_String_ReturnsString()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.String);
        var result = _parser.CoerceValue("hello world", attr);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void CoerceValue_Memo_ReturnsString()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Memo);
        var result = _parser.CoerceValue("long text here", attr);
        Assert.Equal("long text here", result);
    }

    [Fact]
    public void CoerceValue_NullOrEmpty_ReturnsNull()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.String);
        Assert.Null(_parser.CoerceValue(null, attr));
        Assert.Null(_parser.CoerceValue("", attr));
    }

    #endregion

    #region Integer Tests

    [Fact]
    public void CoerceValue_Integer_ParsesValidInt()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Integer);
        var result = _parser.CoerceValue("42", attr);
        Assert.Equal(42, result);
    }

    [Fact]
    public void CoerceValue_Integer_ReturnsNullForInvalid()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Integer);
        Assert.Null(_parser.CoerceValue("not a number", attr));
    }

    [Fact]
    public void CoerceValue_Integer_HandlesNegative()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Integer);
        var result = _parser.CoerceValue("-123", attr);
        Assert.Equal(-123, result);
    }

    #endregion

    #region BigInt Tests

    [Fact]
    public void CoerceValue_BigInt_ParsesValidLong()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.BigInt);
        var result = _parser.CoerceValue("9223372036854775807", attr);
        Assert.Equal(9223372036854775807L, result);
    }

    #endregion

    #region Decimal Tests

    [Fact]
    public void CoerceValue_Decimal_ParsesWithInvariantCulture()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Decimal);
        var result = _parser.CoerceValue("123.45", attr);
        Assert.Equal(123.45m, result);
    }

    [Fact]
    public void CoerceValue_Decimal_HandlesNegative()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Decimal);
        var result = _parser.CoerceValue("-999.99", attr);
        Assert.Equal(-999.99m, result);
    }

    #endregion

    #region Double Tests

    [Fact]
    public void CoerceValue_Double_ParsesValidDouble()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Double);
        var result = _parser.CoerceValue("3.14159", attr);
        Assert.Equal(3.14159, result);
    }

    #endregion

    #region Money Tests

    [Fact]
    public void CoerceValue_Money_ParsesCurrency()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Money);
        var result = _parser.CoerceValue("100.50", attr);
        Assert.IsType<Money>(result);
        Assert.Equal(100.50m, ((Money)result!).Value);
    }

    [Fact]
    public void CoerceValue_Money_HandlesDollarSign()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Money);
        var result = _parser.CoerceValue("$100.50", attr);
        Assert.IsType<Money>(result);
        Assert.Equal(100.50m, ((Money)result!).Value);
    }

    #endregion

    #region Boolean Tests

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData("1", true)]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    public void CoerceValue_Boolean_ParsesTrueValues(string input, bool expected)
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Boolean);
        var result = _parser.CoerceValue(input, attr);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    [InlineData("No", false)]
    [InlineData("0", false)]
    [InlineData("n", false)]
    [InlineData("N", false)]
    public void CoerceValue_Boolean_ParsesFalseValues(string input, bool expected)
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Boolean);
        var result = _parser.CoerceValue(input, attr);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CoerceValue_Boolean_ReturnsNullForInvalid()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Boolean);
        Assert.Null(_parser.CoerceValue("maybe", attr));
    }

    #endregion

    #region DateTime Tests

    [Fact]
    public void CoerceValue_DateTime_ParsesIsoFormat()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.DateTime);
        var result = _parser.CoerceValue("2024-01-15T10:30:00Z", attr);
        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result!;
        Assert.Equal(2024, dt.Year);
        Assert.Equal(1, dt.Month);
        Assert.Equal(15, dt.Day);
    }

    [Fact]
    public void CoerceValue_DateTime_UsesCustomFormat()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.DateTime);
        var mapping = new ColumnMappingEntry { DateFormat = "MM/dd/yyyy" };
        var result = _parser.CoerceValue("01/15/2024", attr, mapping);
        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result!;
        Assert.Equal(2024, dt.Year);
        Assert.Equal(1, dt.Month);
        Assert.Equal(15, dt.Day);
    }

    [Fact]
    public void CoerceValue_DateTime_ReturnsNullForInvalidFormat()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.DateTime);
        var mapping = new ColumnMappingEntry { DateFormat = "yyyy-MM-dd" };
        Assert.Null(_parser.CoerceValue("01/15/2024", attr, mapping));
    }

    #endregion

    #region Guid Tests

    [Fact]
    public void CoerceValue_Guid_ParsesValidGuid()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Uniqueidentifier);
        var result = _parser.CoerceValue("a1b2c3d4-e5f6-7890-abcd-ef1234567890", attr);
        Assert.IsType<Guid>(result);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"), result);
    }

    [Fact]
    public void CoerceValue_Guid_ReturnsNullForInvalid()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Uniqueidentifier);
        Assert.Null(_parser.CoerceValue("not-a-guid", attr));
    }

    #endregion

    #region OptionSet Tests

    [Fact]
    public void CoerceValue_OptionSet_ParsesNumericValue()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Picklist);
        var result = _parser.CoerceValue("5", attr);
        Assert.IsType<OptionSetValue>(result);
        Assert.Equal(5, ((OptionSetValue)result!).Value);
    }

    [Fact]
    public void CoerceValue_OptionSet_UsesLabelMap()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Picklist);
        var mapping = new ColumnMappingEntry
        {
            OptionsetMap = new Dictionary<string, int>
            {
                ["Active"] = 1,
                ["Inactive"] = 0
            }
        };
        var result = _parser.CoerceValue("Active", attr, mapping);
        Assert.IsType<OptionSetValue>(result);
        Assert.Equal(1, ((OptionSetValue)result!).Value);
    }

    [Fact]
    public void CoerceValue_OptionSet_ReturnsNullForUnknownLabel()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Picklist);
        Assert.Null(_parser.CoerceValue("Unknown", attr));
    }

    #endregion

    #region Lookup Tests

    [Fact]
    public void CoerceValue_Lookup_ReturnsNull_HandledByResolver()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Lookup);
        var result = _parser.CoerceValue("some-value", attr);
        Assert.Null(result);
    }

    #endregion

    #region IsGuid Tests

    [Fact]
    public void IsGuid_ValidGuid_ReturnsTrue()
    {
        Assert.True(CsvRecordParser.IsGuid("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
    }

    [Fact]
    public void IsGuid_InvalidGuid_ReturnsFalse()
    {
        Assert.False(CsvRecordParser.IsGuid("not-a-guid"));
        Assert.False(CsvRecordParser.IsGuid("Contoso Ltd"));
        Assert.False(CsvRecordParser.IsGuid(""));
        Assert.False(CsvRecordParser.IsGuid(null));
    }

    #endregion

    #region TryCoerceValue Tests

    [Fact]
    public void TryCoerceValue_Success_ReturnsSuccessAndValue()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Integer);
        var (success, value, error) = _parser.TryCoerceValue("42", attr);
        Assert.True(success);
        Assert.Equal(42, value);
        Assert.Null(error);
    }

    [Fact]
    public void TryCoerceValue_Failure_ReturnsFailureAndError()
    {
        var attr = CreateAttributeMetadata(AttributeTypeCode.Integer);
        var (success, value, error) = _parser.TryCoerceValue("not-a-number", attr);
        Assert.False(success);
        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Contains("Cannot convert", error);
    }

    #endregion

    private static AttributeMetadata CreateAttributeMetadata(AttributeTypeCode type)
    {
        // The specialized metadata classes already have the correct AttributeType set
        return type switch
        {
            AttributeTypeCode.String => new StringAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Memo => new MemoAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Integer => new IntegerAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.BigInt => new BigIntAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Decimal => new DecimalAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Double => new DoubleAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Money => new MoneyAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Boolean => new BooleanAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.DateTime => new DateTimeAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Uniqueidentifier => new UniqueIdentifierAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Picklist => new PicklistAttributeMetadata { LogicalName = "test" },
            AttributeTypeCode.Lookup => new LookupAttributeMetadata { LogicalName = "test" },
            _ => new StringAttributeMetadata { LogicalName = "test" }
        };
    }
}
