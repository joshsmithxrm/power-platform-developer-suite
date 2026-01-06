using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Formats query values for display with type-aware rendering.
/// </summary>
internal static class ValueFormatter
{
    private const int GuidSuffixLength = 8;
    private const int MaxTextWidth = 80;

    /// <summary>
    /// Formats a query value based on its column type.
    /// </summary>
    public static string Format(QueryValue? value, QueryColumn column)
    {
        if (value?.Value == null)
        {
            return Styles.MutedText("(null)");
        }

        return column.DataType switch
        {
            QueryColumnType.Guid => FormatGuid(value),
            QueryColumnType.Lookup => FormatLookup(value),
            QueryColumnType.DateTime => FormatDateTime(value),
            QueryColumnType.Money => FormatMoney(value),
            QueryColumnType.Boolean => FormatBoolean(value),
            QueryColumnType.OptionSet => FormatOptionSet(value),
            QueryColumnType.MultiSelectOptionSet => FormatMultiSelectOptionSet(value),
            QueryColumnType.Memo => FormatMemo(value),
            _ => FormatDefault(value)
        };
    }

    /// <summary>
    /// Formats a value for table display (more compact).
    /// </summary>
    public static string FormatForTable(QueryValue? value, QueryColumn column, int maxWidth)
    {
        if (value?.Value == null)
        {
            return Styles.MutedText("-");
        }

        var formatted = Format(value, column);

        // Strip markup to measure actual length
        var plainText = Markup.Remove(formatted);
        if (plainText.Length <= maxWidth)
        {
            return formatted;
        }

        // Truncate for table display
        return Markup.Escape(plainText[..(maxWidth - 3)]) + Styles.MutedText("...");
    }

    private static string FormatGuid(QueryValue value)
    {
        if (value.Value is Guid guid)
        {
            var str = guid.ToString("N"); // No dashes: 32 chars
            return Styles.MutedText("...") + str[^GuidSuffixLength..];
        }

        // Handle string representation of GUID
        var strValue = value.Value?.ToString();
        if (!string.IsNullOrEmpty(strValue) && Guid.TryParse(strValue, out var parsed))
        {
            var str = parsed.ToString("N");
            return Styles.MutedText("...") + str[^GuidSuffixLength..];
        }

        return Markup.Escape(strValue ?? "");
    }

    private static string FormatLookup(QueryValue value)
    {
        var displayName = value.FormattedValue ?? value.Value?.ToString() ?? "";

        if (!string.IsNullOrEmpty(value.LookupEntityType))
        {
            var guidSuffix = value.LookupEntityId.HasValue
                ? $"...{value.LookupEntityId.Value.ToString("N")[^GuidSuffixLength..]}"
                : "";

            var entityHint = !string.IsNullOrEmpty(guidSuffix)
                ? $"{value.LookupEntityType} {guidSuffix}"
                : value.LookupEntityType;

            return $"{Markup.Escape(displayName)} {Styles.MutedText($"({entityHint})")}";
        }

        return Markup.Escape(displayName);
    }

    private static string FormatDateTime(QueryValue value)
    {
        // Prefer formatted value if available
        if (!string.IsNullOrEmpty(value.FormattedValue))
        {
            return Markup.Escape(value.FormattedValue);
        }

        if (value.Value is DateTime dt)
        {
            return Markup.Escape(dt.ToString("g")); // General date/time
        }

        return Markup.Escape(value.Value?.ToString() ?? "");
    }

    private static string FormatMoney(QueryValue value)
    {
        // Prefer formatted value (includes currency symbol)
        if (!string.IsNullOrEmpty(value.FormattedValue))
        {
            return Markup.Escape(value.FormattedValue);
        }

        if (value.Value is decimal d)
        {
            return Markup.Escape(d.ToString("C"));
        }

        return Markup.Escape(value.Value?.ToString() ?? "");
    }

    private static string FormatBoolean(QueryValue value)
    {
        // Prefer formatted value (Yes/No)
        if (!string.IsNullOrEmpty(value.FormattedValue))
        {
            return Markup.Escape(value.FormattedValue);
        }

        if (value.Value is bool b)
        {
            return b ? Styles.SuccessText("Yes") : Styles.MutedText("No");
        }

        return Markup.Escape(value.Value?.ToString() ?? "");
    }

    private static string FormatOptionSet(QueryValue value)
    {
        // Prefer formatted value (option label)
        if (!string.IsNullOrEmpty(value.FormattedValue))
        {
            return Markup.Escape(value.FormattedValue);
        }

        return Markup.Escape(value.Value?.ToString() ?? "");
    }

    private static string FormatMultiSelectOptionSet(QueryValue value)
    {
        // Prefer formatted value (comma-separated labels)
        if (!string.IsNullOrEmpty(value.FormattedValue))
        {
            return Markup.Escape(value.FormattedValue);
        }

        // Handle array of values
        if (value.Value is IEnumerable<object> items)
        {
            return Markup.Escape(string.Join(", ", items));
        }

        return Markup.Escape(value.Value?.ToString() ?? "");
    }

    private static string FormatMemo(QueryValue value)
    {
        var text = value.Value?.ToString() ?? "";

        if (text.Length > MaxTextWidth)
        {
            // For long text, show truncated with indicator
            var truncated = text[..(MaxTextWidth - 3)];
            // Remove any partial words at the end
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > MaxTextWidth / 2)
            {
                truncated = truncated[..lastSpace];
            }
            return Markup.Escape(truncated) + Styles.MutedText("...");
        }

        return Markup.Escape(text);
    }

    private static string FormatDefault(QueryValue value)
    {
        // Use formatted value if available, otherwise raw value
        var display = value.FormattedValue ?? value.Value?.ToString() ?? "";
        return Markup.Escape(display);
    }

    /// <summary>
    /// Gets the raw display value without markup for width calculations.
    /// </summary>
    public static string GetPlainValue(QueryValue? value, QueryColumn column)
    {
        if (value?.Value == null)
        {
            return "(null)";
        }

        // For GUIDs, return the truncated format (use actual GUID, not display string)
        if (column.DataType == QueryColumnType.Guid && value.Value is Guid guid)
        {
            return $"...{guid.ToString("N")[^GuidSuffixLength..]}";
        }

        return value.FormattedValue ?? value.Value.ToString() ?? "";
    }
}
