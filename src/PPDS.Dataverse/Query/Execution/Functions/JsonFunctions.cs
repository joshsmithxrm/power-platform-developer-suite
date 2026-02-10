using System;
using System.Globalization;
using System.Text.Json;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL JSON functions evaluated client-side using System.Text.Json.
/// JSON path syntax: $.property.nested (simplified JSONPath).
/// </summary>
public static class JsonFunctions
{
    /// <summary>
    /// Registers all JSON functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("JSON_VALUE", new JsonValueFunction());
        registry.Register("JSON_QUERY", new JsonQueryFunction());
        registry.Register("JSON_PATH_EXISTS", new JsonPathExistsFunction());
        registry.Register("JSON_MODIFY", new JsonModifyFunction());
        registry.Register("ISJSON", new IsJsonFunction());
    }

    /// <summary>
    /// Returns NULL if any argument is NULL.
    /// </summary>
    private static bool HasNull(object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is null) return true;
        }
        return false;
    }

    /// <summary>
    /// Helper: converts an argument to string.
    /// </summary>
    private static string? AsString(object? value)
    {
        if (value is null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Navigates a JSON document using a simplified JSONPath.
    /// Supports $.property.nested and $.property[0] syntax.
    /// </summary>
    private static JsonElement? NavigatePath(JsonDocument doc, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return doc.RootElement;
        }

        // Strip leading "$." or "$"
        var segments = path;
        if (segments.StartsWith("$.", StringComparison.Ordinal))
        {
            segments = segments.Substring(2);
        }
        else if (segments.StartsWith("$", StringComparison.Ordinal))
        {
            segments = segments.Substring(1);
        }

        if (string.IsNullOrEmpty(segments))
        {
            return doc.RootElement;
        }

        JsonElement current = doc.RootElement;

        // Split on '.' but handle array indexers
        int pos = 0;
        while (pos < segments.Length)
        {
            // Find next segment boundary
            int dotPos = segments.IndexOf('.', pos);
            int bracketPos = segments.IndexOf('[', pos);

            string segment;
            if (dotPos < 0 && bracketPos < 0)
            {
                segment = segments.Substring(pos);
                pos = segments.Length;
            }
            else if (bracketPos >= 0 && (dotPos < 0 || bracketPos < dotPos))
            {
                // Handle array indexer
                if (bracketPos > pos)
                {
                    // Property before bracket
                    segment = segments.Substring(pos, bracketPos - pos);
                    if (!TryGetProperty(current, segment, out current))
                        return null;
                }

                int closeBracket = segments.IndexOf(']', bracketPos);
                if (closeBracket < 0) return null;

                var indexStr = segments.Substring(bracketPos + 1, closeBracket - bracketPos - 1);
                if (!int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    return null;

                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return null;

                current = current[index];
                pos = closeBracket + 1;
                if (pos < segments.Length && segments[pos] == '.')
                    pos++;
                continue;
            }
            else
            {
                segment = segments.Substring(pos, dotPos - pos);
                pos = dotPos + 1;
            }

            if (!string.IsNullOrEmpty(segment))
            {
                if (!TryGetProperty(current, segment, out current))
                    return null;
            }
        }

        return current;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            result = default;
            return false;
        }
        return element.TryGetProperty(propertyName, out result);
    }

    /// <summary>
    /// Extracts the scalar value from a JsonElement as a .NET type.
    /// </summary>
    private static string? GetScalarValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null // Objects and arrays are not scalar
        };
    }

    // ── JSON_VALUE ──────────────────────────────────────────────────────
    /// <summary>
    /// JSON_VALUE(json_string, path) - extracts a scalar value from JSON.
    /// Returns NULL for objects/arrays (use JSON_QUERY for those).
    /// </summary>
    private sealed class JsonValueFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var jsonStr = AsString(args[0])!;
            var path = AsString(args[1])!;

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var element = NavigatePath(doc, path);
                if (element is null) return null;
                return GetScalarValue(element.Value);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // ── JSON_QUERY ──────────────────────────────────────────────────────
    /// <summary>
    /// JSON_QUERY(json_string, path) - extracts an object or array from JSON.
    /// Returns NULL for scalar values (use JSON_VALUE for those).
    /// </summary>
    private sealed class JsonQueryFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (args[0] is null) return null;
            var jsonStr = AsString(args[0])!;
            var path = args.Length >= 2 && args[1] is not null ? AsString(args[1])! : "$";

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var element = NavigatePath(doc, path);
                if (element is null) return null;

                var el = element.Value;
                if (el.ValueKind != JsonValueKind.Object && el.ValueKind != JsonValueKind.Array)
                    return null;

                return el.GetRawText();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // ── JSON_PATH_EXISTS ────────────────────────────────────────────────
    /// <summary>
    /// JSON_PATH_EXISTS(json_string, path) - returns 1 if the path exists, 0 otherwise.
    /// </summary>
    private sealed class JsonPathExistsFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var jsonStr = AsString(args[0])!;
            var path = AsString(args[1])!;

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var element = NavigatePath(doc, path);
                return element.HasValue ? 1 : 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }
    }

    // ── JSON_MODIFY ─────────────────────────────────────────────────────
    /// <summary>
    /// JSON_MODIFY(json_string, path, new_value) - modifies a value in JSON.
    /// Returns the modified JSON string.
    /// </summary>
    private sealed class JsonModifyFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (args[0] is null) return null;
            if (args[1] is null) return null;
            var jsonStr = AsString(args[0])!;
            var path = AsString(args[1])!;
            var newValue = args[2]; // Can be null (sets to JSON null)

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);

                // Parse the path to find the property to modify
                var segments = path;
                if (segments.StartsWith("$.", StringComparison.Ordinal))
                {
                    segments = segments.Substring(2);
                }
                else if (segments.StartsWith("$", StringComparison.Ordinal))
                {
                    segments = segments.Substring(1);
                }

                // Simple implementation: only supports single-level and dot-separated paths
                var pathParts = segments.Split('.');
                if (pathParts.Length == 0) return jsonStr;

                // Re-serialize the document with the modification
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    WriteModified(writer, doc.RootElement, pathParts, 0, newValue);
                }
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return jsonStr;
            }
        }

        private static void WriteModified(
            Utf8JsonWriter writer, JsonElement element, string[] pathParts, int depth, object? newValue)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                bool found = false;
                foreach (var prop in element.EnumerateObject())
                {
                    if (depth < pathParts.Length && string.Equals(prop.Name, pathParts[depth], StringComparison.Ordinal))
                    {
                        found = true;
                        writer.WritePropertyName(prop.Name);
                        if (depth == pathParts.Length - 1)
                        {
                            // This is the target property - write new value
                            WriteValue(writer, newValue);
                        }
                        else
                        {
                            // Navigate deeper
                            WriteModified(writer, prop.Value, pathParts, depth + 1, newValue);
                        }
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                // If property not found at the target level, add it
                if (!found && depth == pathParts.Length - 1)
                {
                    writer.WritePropertyName(pathParts[depth]);
                    WriteValue(writer, newValue);
                }
                writer.WriteEndObject();
            }
            else
            {
                element.WriteTo(writer);
            }
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else if (value is string s)
            {
                // Try to parse as JSON first (for nested objects/arrays)
                try
                {
                    using var innerDoc = JsonDocument.Parse(s);
                    innerDoc.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteStringValue(s);
                }
            }
            else if (value is int i)
            {
                writer.WriteNumberValue(i);
            }
            else if (value is long l)
            {
                writer.WriteNumberValue(l);
            }
            else if (value is decimal d)
            {
                writer.WriteNumberValue(d);
            }
            else if (value is double dbl)
            {
                writer.WriteNumberValue(dbl);
            }
            else if (value is bool b)
            {
                writer.WriteBooleanValue(b);
            }
            else
            {
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }

    // ── ISJSON ──────────────────────────────────────────────────────────
    /// <summary>
    /// ISJSON(string) - returns 1 if the string is valid JSON, 0 otherwise.
    /// </summary>
    private sealed class IsJsonFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            try
            {
                using var doc = JsonDocument.Parse(str);
                return 1;
            }
            catch (JsonException)
            {
                return 0;
            }
        }
    }
}
