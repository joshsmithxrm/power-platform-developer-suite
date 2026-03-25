using PPDS.Dataverse.Query;

namespace PPDS.Mcp.Tools;

/// <summary>
/// Shared helpers for extracting typed values from query result records.
/// </summary>
internal static class QueryValueExtensions
{
    /// <summary>
    /// Gets a <see cref="Guid"/> value, returning <see cref="Guid.Empty"/> if not found or not parseable.
    /// </summary>
    public static Guid GetGuid(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return Guid.Empty;
    }

    /// <summary>
    /// Gets a nullable <see cref="Guid"/> value, returning <c>null</c> if not found or not parseable.
    /// </summary>
    public static Guid? GetGuidNullable(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Gets a string value, returning <c>null</c> if not found.
    /// </summary>
    public static string? GetString(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
            return qv.Value?.ToString();
        return null;
    }

    /// <summary>
    /// Gets the formatted display value, falling back to the raw value string.
    /// Returns <c>null</c> if the key is not found.
    /// </summary>
    public static string? GetFormatted(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
            return qv.FormattedValue ?? qv.Value?.ToString();
        return null;
    }

    /// <summary>
    /// Gets a boolean value, returning <c>false</c> if not found or not parseable.
    /// </summary>
    public static bool GetBool(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is bool b) return b;
            if (bool.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    /// <summary>
    /// Gets an integer value, returning <c>0</c> if not found or not parseable.
    /// </summary>
    public static int GetInt(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is int i) return i;
            if (int.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    /// <summary>
    /// Gets a nullable integer value, returning <c>null</c> if not found or not parseable.
    /// </summary>
    public static int? GetIntNullable(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is int i) return i;
            if (int.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Gets a nullable <see cref="DateTime"/> value, returning <c>null</c> if not found or not parseable.
    /// </summary>
    public static DateTime? GetDateTime(this IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is DateTime dt) return dt;
            if (DateTime.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Escapes a string for safe use in FetchXML attribute values.
    /// </summary>
    public static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
