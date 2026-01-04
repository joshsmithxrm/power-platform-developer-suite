using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Parses CSV values and coerces them to Dataverse attribute types.
/// </summary>
public sealed class CsvRecordParser
{
    private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "yes", "1", "y"
    };

    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "false", "no", "0", "n"
    };

    /// <summary>
    /// Coerces a CSV string value to the appropriate Dataverse type based on attribute metadata.
    /// </summary>
    /// <param name="csvValue">The raw string value from the CSV.</param>
    /// <param name="attributeMetadata">The target attribute metadata.</param>
    /// <param name="mapping">Optional column mapping with additional configuration.</param>
    /// <returns>The coerced value, or null if the value is empty or cannot be converted.</returns>
    public object? CoerceValue(
        string? csvValue,
        AttributeMetadata attributeMetadata,
        ColumnMappingEntry? mapping = null)
    {
        if (string.IsNullOrEmpty(csvValue))
        {
            return null;
        }

        return attributeMetadata.AttributeType switch
        {
            AttributeTypeCode.String or AttributeTypeCode.Memo => csvValue,

            AttributeTypeCode.Integer => CoerceInteger(csvValue),

            AttributeTypeCode.BigInt => CoerceBigInt(csvValue),

            AttributeTypeCode.Decimal => CoerceDecimal(csvValue),

            AttributeTypeCode.Double => CoerceDouble(csvValue),

            AttributeTypeCode.Money => CoerceMoney(csvValue),

            AttributeTypeCode.Boolean => CoerceBoolean(csvValue),

            AttributeTypeCode.DateTime => CoerceDateTime(csvValue, mapping?.DateFormat),

            AttributeTypeCode.Uniqueidentifier => CoerceGuid(csvValue),

            AttributeTypeCode.Picklist or
            AttributeTypeCode.State or
            AttributeTypeCode.Status => CoerceOptionSet(csvValue, mapping?.OptionsetMap),

            // Lookups are handled separately by LookupResolver
            AttributeTypeCode.Lookup or
            AttributeTypeCode.Customer or
            AttributeTypeCode.Owner => null,

            _ => csvValue // Unknown types passed as string
        };
    }

    /// <summary>
    /// Attempts to coerce a value, returning success status and any error.
    /// </summary>
    public (bool Success, object? Value, string? ErrorMessage) TryCoerceValue(
        string? csvValue,
        AttributeMetadata attributeMetadata,
        ColumnMappingEntry? mapping = null)
    {
        if (string.IsNullOrEmpty(csvValue))
        {
            return (true, null, null);
        }

        try
        {
            var result = attributeMetadata.AttributeType switch
            {
                AttributeTypeCode.String or AttributeTypeCode.Memo => (true, csvValue, null),

                AttributeTypeCode.Integer => TryCoerceInteger(csvValue),

                AttributeTypeCode.BigInt => TryCoerceBigInt(csvValue),

                AttributeTypeCode.Decimal => TryCoerceDecimal(csvValue),

                AttributeTypeCode.Double => TryCoerceDouble(csvValue),

                AttributeTypeCode.Money => TryCoerceMoney(csvValue),

                AttributeTypeCode.Boolean => TryCoerceBoolean(csvValue),

                AttributeTypeCode.DateTime => TryCoerceDateTime(csvValue, mapping?.DateFormat),

                AttributeTypeCode.Uniqueidentifier => TryCoerceGuid(csvValue),

                AttributeTypeCode.Picklist or
                AttributeTypeCode.State or
                AttributeTypeCode.Status => TryCoerceOptionSet(csvValue, mapping?.OptionsetMap),

                AttributeTypeCode.Lookup or
                AttributeTypeCode.Customer or
                AttributeTypeCode.Owner => (true, null, null), // Handled by LookupResolver

                _ => (true, csvValue, null)
            };

            return result;
        }
        catch (FormatException ex)
        {
            return (false, null, ex.Message);
        }
        catch (OverflowException ex)
        {
            return (false, null, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static int? CoerceInteger(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static (bool, object?, string?) TryCoerceInteger(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return (true, result, null);
        }
        return (false, null, $"Cannot convert '{value}' to integer");
    }

    private static long? CoerceBigInt(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static (bool, object?, string?) TryCoerceBigInt(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return (true, result, null);
        }
        return (false, null, $"Cannot convert '{value}' to big integer");
    }

    private static decimal? CoerceDecimal(string value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static (bool, object?, string?) TryCoerceDecimal(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            return (true, result, null);
        }
        return (false, null, $"Cannot convert '{value}' to decimal");
    }

    private static double? CoerceDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static (bool, object?, string?) TryCoerceDouble(string value)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            return (true, result, null);
        }
        return (false, null, $"Cannot convert '{value}' to double");
    }

    private static Money? CoerceMoney(string value)
    {
        // Remove currency symbols and parse
        var cleanValue = value.Trim('$', '€', '£', '¥', ' ');
        return decimal.TryParse(cleanValue, NumberStyles.Currency, CultureInfo.InvariantCulture, out var result)
            ? new Money(result)
            : null;
    }

    private static (bool, object?, string?) TryCoerceMoney(string value)
    {
        var cleanValue = value.Trim('$', '€', '£', '¥', ' ');
        if (decimal.TryParse(cleanValue, NumberStyles.Currency, CultureInfo.InvariantCulture, out var result))
        {
            return (true, new Money(result), null);
        }
        return (false, null, $"Cannot convert '{value}' to money");
    }

    private static bool? CoerceBoolean(string value)
    {
        if (TrueValues.Contains(value))
        {
            return true;
        }
        if (FalseValues.Contains(value))
        {
            return false;
        }
        return null;
    }

    private static (bool, object?, string?) TryCoerceBoolean(string value)
    {
        if (TrueValues.Contains(value))
        {
            return (true, true, null);
        }
        if (FalseValues.Contains(value))
        {
            return (true, false, null);
        }
        return (false, null, $"Cannot convert '{value}' to boolean. Use true/false, yes/no, 1/0, or y/n.");
    }

    private static DateTime? CoerceDateTime(string value, string? format)
    {
        if (!string.IsNullOrEmpty(format))
        {
            return DateTime.TryParseExact(
                value,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dtExact) ? dtExact : null;
        }

        // Flexible parsing with common formats
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }

    private static (bool, object?, string?) TryCoerceDateTime(string value, string? format)
    {
        if (!string.IsNullOrEmpty(format))
        {
            if (DateTime.TryParseExact(
                value,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dtExact))
            {
                return (true, dtExact, null);
            }
            return (false, null, $"Cannot convert '{value}' to datetime using format '{format}'");
        }

        if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt))
        {
            return (true, dt, null);
        }
        return (false, null, $"Cannot convert '{value}' to datetime");
    }

    private static Guid? CoerceGuid(string value)
    {
        return Guid.TryParse(value, out var result) ? result : null;
    }

    private static (bool, object?, string?) TryCoerceGuid(string value)
    {
        if (Guid.TryParse(value, out var result))
        {
            return (true, result, null);
        }
        return (false, null, $"Cannot convert '{value}' to GUID");
    }

    private static OptionSetValue? CoerceOptionSet(string value, Dictionary<string, int>? labelMap)
    {
        // First try label map from mapping file
        if (labelMap != null && labelMap.TryGetValue(value, out var mapped))
        {
            return new OptionSetValue(mapped);
        }

        // Then try numeric value
        if (int.TryParse(value, out var numericValue))
        {
            return new OptionSetValue(numericValue);
        }

        return null;
    }

    private static (bool, object?, string?) TryCoerceOptionSet(string value, Dictionary<string, int>? labelMap)
    {
        // First try label map from mapping file
        if (labelMap != null && labelMap.TryGetValue(value, out var mapped))
        {
            return (true, new OptionSetValue(mapped), null);
        }

        // Then try numeric value
        if (int.TryParse(value, out var numericValue))
        {
            return (true, new OptionSetValue(numericValue), null);
        }

        var hint = labelMap != null && labelMap.Count > 0
            ? $" Valid labels: {string.Join(", ", labelMap.Keys.Take(5))}"
            : " Use numeric value or add optionsetMap in mapping file.";

        return (false, null, $"Cannot convert '{value}' to optionset.{hint}");
    }

    /// <summary>
    /// Checks if a value appears to be a GUID.
    /// </summary>
    public static bool IsGuid(string? value)
    {
        return !string.IsNullOrEmpty(value) && Guid.TryParse(value, out _);
    }
}
