namespace PPDS.Dataverse.Query;

/// <summary>
/// Represents the data type of a column in a query result.
/// </summary>
public enum QueryColumnType
{
    /// <summary>Unknown or undetected type.</summary>
    Unknown,

    /// <summary>Text string value.</summary>
    String,

    /// <summary>Whole number (32-bit integer).</summary>
    Integer,

    /// <summary>Whole number (64-bit integer).</summary>
    BigInt,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>Floating-point number.</summary>
    Double,

    /// <summary>Currency value with formatting.</summary>
    Money,

    /// <summary>Boolean true/false value.</summary>
    Boolean,

    /// <summary>Date and time value.</summary>
    DateTime,

    /// <summary>Globally unique identifier.</summary>
    Guid,

    /// <summary>Reference to another entity record (lookup).</summary>
    Lookup,

    /// <summary>Option set (picklist) value.</summary>
    OptionSet,

    /// <summary>Multi-select option set value.</summary>
    MultiSelectOptionSet,

    /// <summary>Entity image/file data.</summary>
    Image,

    /// <summary>Memo/multi-line text.</summary>
    Memo,

    /// <summary>Aliased aggregate value (from COUNT, SUM, etc.).</summary>
    AliasedValue
}
