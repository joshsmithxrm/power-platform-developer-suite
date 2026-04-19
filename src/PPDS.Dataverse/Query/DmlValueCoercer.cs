using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Coerces raw CLR values from SQL DML into Dataverse SDK-typed values
/// (<see cref="EntityReference"/> for lookups, <see cref="OptionSetValue"/> for choices,
/// <see cref="Money"/> for currency, etc.) using attribute metadata.
/// </summary>
/// <remarks>
/// SQL literals compiled from ScriptDom are primitive CLR values (int, decimal, string).
/// Dataverse's <c>CreateMultiple</c>/<c>UpdateMultiple</c> reject primitives for typed
/// attributes (lookup, picklist, money) — they require SDK wrapper types. This helper
/// bridges that gap. When metadata is unavailable or a type is not recognized, it returns
/// the value unchanged so callers degrade gracefully to today's behavior.
/// </remarks>
public static class DmlValueCoercer
{
    /// <summary>
    /// Coerces <paramref name="value"/> to the Dataverse SDK type implied by
    /// <paramref name="attribute"/>. Returns the input unchanged when coercion is not
    /// possible (null input, null metadata, unsupported type).
    /// </summary>
    /// <param name="value">The raw CLR value from a compiled SQL expression.</param>
    /// <param name="attribute">Target attribute metadata, or null if unknown.</param>
    /// <returns>The coerced value, or <paramref name="value"/> unchanged.</returns>
    public static object? Coerce(object? value, AttributeMetadataDto? attribute)
    {
        if (value is null || attribute is null)
            return value;

        // AttributeMetadataDto.AttributeType is AttributeTypeCode.ToString().
        switch (attribute.AttributeType)
        {
            case "Lookup":
            case "Customer":
            case "Owner":
                return CoerceLookup(value, attribute);

            case "Picklist":
            case "State":
            case "Status":
                return CoerceOptionSet(value);

            case "Money":
                return CoerceMoney(value);

            case "Boolean":
                return CoerceBoolean(value);

            case "Integer":
                return CoerceInt(value);

            case "BigInt":
                return CoerceLong(value);

            case "Decimal":
                return CoerceDecimal(value);

            case "Double":
                return CoerceDouble(value);

            case "DateTime":
                return CoerceDateTime(value);

            case "Uniqueidentifier":
                return CoerceGuid(value);

            case "String":
            case "Memo":
                return value is string ? value : value.ToString();

            case "Virtual":
                // MultiSelectPicklist is modeled as Virtual with AttributeTypeName="MultiSelectPicklistType".
                // SQL DML does not yet support writing OptionSetValueCollection; fail loudly rather than
                // passing a raw int/string through to Dataverse (which produces an opaque SDK error).
                if (attribute.AttributeTypeName == "MultiSelectPicklistType")
                {
                    throw new QueryExecutionException(
                        QueryErrorCode.TypeMismatch,
                        $"Multi-select choice column '{attribute.LogicalName}' is not yet supported by SQL DML. " +
                        "Use the Dataverse SDK or a targeted import for multi-select writes.");
                }
                return value;

            default:
                return value;
        }
    }

    private static EntityReference CoerceLookup(object value, AttributeMetadataDto attribute)
    {
        if (value is EntityReference existing)
            return existing;

        var target = ResolveLookupTarget(attribute);
        var id = CoerceGuid(value);
        if (id is not Guid guid)
        {
            throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"Cannot convert value '{value}' of type {value.GetType().Name} to a GUID " +
                $"for lookup attribute '{attribute.LogicalName}'.");
        }
        return new EntityReference(target, guid);
    }

    private static string ResolveLookupTarget(AttributeMetadataDto attribute)
    {
        var targets = attribute.Targets;
        if (targets == null || targets.Count == 0)
        {
            throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"Lookup attribute '{attribute.LogicalName}' has no target entity in metadata. " +
                "Cannot construct an EntityReference.");
        }

        if (targets.Count == 1)
            return targets[0];

        throw new QueryExecutionException(
            QueryErrorCode.TypeMismatch,
            $"Lookup attribute '{attribute.LogicalName}' is polymorphic with targets [{string.Join(", ", targets)}]. " +
            "SQL DML cannot currently disambiguate polymorphic lookups. Pass an EntityReference via a parameter, " +
            "or set the value outside SQL DML.");
    }

    private static OptionSetValue CoerceOptionSet(object value)
    {
        if (value is OptionSetValue existing)
            return existing;
        return new OptionSetValue(ToInt32(value));
    }

    private static Money CoerceMoney(object value)
    {
        if (value is Money existing)
            return existing;
        return new Money(ToDecimal(value));
    }

    private static bool CoerceBoolean(object value)
    {
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when s == "1" => true,
            string s when s == "0" => false,
            IConvertible conv => Convert.ToInt64(conv, CultureInfo.InvariantCulture) != 0,
            _ => throw TypeMismatch(value, "Boolean")
        };
    }

    private static int CoerceInt(object value) => ToInt32(value);

    private static long CoerceLong(object value)
    {
        return value switch
        {
            long l => l,
            string s => long.Parse(s, CultureInfo.InvariantCulture),
            IConvertible conv => Convert.ToInt64(conv, CultureInfo.InvariantCulture),
            _ => throw TypeMismatch(value, "Int64")
        };
    }

    private static decimal CoerceDecimal(object value) => ToDecimal(value);

    private static double CoerceDouble(object value)
    {
        return value switch
        {
            double d => d,
            string s => double.Parse(s, CultureInfo.InvariantCulture),
            IConvertible conv => Convert.ToDouble(conv, CultureInfo.InvariantCulture),
            _ => throw TypeMismatch(value, "Double")
        };
    }

    /// <summary>
    /// Parses DateTime values for Dataverse DateTime attributes.
    /// Uses <see cref="DateTimeStyles.RoundtripKind"/>: strings with a zone designator
    /// (e.g. <c>"...Z"</c>, <c>"...+05:00"</c>) keep their <see cref="DateTimeKind.Utc"/>
    /// or Local kind; unqualified strings parse as <see cref="DateTimeKind.Unspecified"/>,
    /// which the Dataverse SDK serializes per the column's <c>DateTimeBehavior</c>
    /// (UserLocal / DateOnly / TimeZoneIndependent). Forcing UTC here would silently
    /// shift UserLocal columns by the caller's offset.
    /// </summary>
    private static DateTime CoerceDateTime(object value)
    {
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => throw TypeMismatch(value, "DateTime")
        };
    }

    private static object CoerceGuid(object value)
    {
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var g) => g,
            _ => throw TypeMismatch(value, "Guid")
        };
    }

    private static int ToInt32(object value)
    {
        return value switch
        {
            int i => i,
            string s => int.Parse(s, CultureInfo.InvariantCulture),
            IConvertible conv => Convert.ToInt32(conv, CultureInfo.InvariantCulture),
            _ => throw TypeMismatch(value, "Int32")
        };
    }

    private static decimal ToDecimal(object value)
    {
        return value switch
        {
            decimal d => d,
            string s => decimal.Parse(s, CultureInfo.InvariantCulture),
            IConvertible conv => Convert.ToDecimal(conv, CultureInfo.InvariantCulture),
            _ => throw TypeMismatch(value, "Decimal")
        };
    }

    private static QueryExecutionException TypeMismatch(object value, string targetType) =>
        new(QueryErrorCode.TypeMismatch,
            $"Cannot convert value '{value}' of type {value.GetType().Name} to {targetType}.");
}
