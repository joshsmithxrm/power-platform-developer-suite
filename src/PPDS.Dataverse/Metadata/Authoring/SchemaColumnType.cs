namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Defines the supported column types for schema authoring operations.
/// </summary>
public enum SchemaColumnType
{
    /// <summary>Single-line text.</summary>
    String,

    /// <summary>Multi-line text.</summary>
    Memo,

    /// <summary>Whole number.</summary>
    Integer,

    /// <summary>Big integer (64-bit).</summary>
    BigInt,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>Floating-point number.</summary>
    Double,

    /// <summary>Currency.</summary>
    Money,

    /// <summary>Two-option (yes/no).</summary>
    Boolean,

    /// <summary>Date and time.</summary>
    DateTime,

    /// <summary>Single-select choice.</summary>
    Choice,

    /// <summary>Multi-select choices.</summary>
    Choices,

    /// <summary>Image column.</summary>
    Image,

    /// <summary>File column.</summary>
    File,

    /// <summary>Lookup (foreign key reference).</summary>
    Lookup,
}
