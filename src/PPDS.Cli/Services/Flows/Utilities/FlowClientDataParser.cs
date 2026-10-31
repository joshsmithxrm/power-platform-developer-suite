using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PPDS.Cli.Services.Flows.Utilities;

/// <summary>
/// Parses flow client data to extract connection reference logical names.
/// </summary>
public static class FlowClientDataParser
{
    /// <summary>
    /// Parses the explicit solution connection-reference mapping used for activation.
    /// Connector aliases are not guessed to be Dataverse logical names.
    /// </summary>
    public static (bool Verified, List<string> Names, string? Error) ParseForActivation(string? clientData)
    {
        if (string.IsNullOrWhiteSpace(clientData))
            return (false, [], "Flow dependency definition is missing.");
        try
        {
            using var doc = JsonDocument.Parse(clientData);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, [], "Flow dependency definition must be an object.");
            var root = doc.RootElement;
            JsonElement references = default;
            if (root.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("connectionReferences", out var nested))
                references = nested;
            else if (root.TryGetProperty("connectionReferences", out var top))
                references = top;
            else if (root.TryGetProperty("definition", out var definition) &&
                definition.ValueKind == JsonValueKind.Object &&
                definition.TryGetProperty("connectionReferences", out var alternate))
                references = alternate;
            if (references.ValueKind != JsonValueKind.Object)
                return (false, [], "Explicit connectionReferences mapping is missing or invalid.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in references.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    return (false, [], "A connection-reference mapping is invalid.");
                string? logicalName = null;
                if (entry.Value.TryGetProperty("connectionReferenceLogicalName", out var direct) &&
                    direct.ValueKind == JsonValueKind.String)
                    logicalName = direct.GetString();
                else if (entry.Value.TryGetProperty("connection", out var connection) &&
                    connection.ValueKind == JsonValueKind.Object &&
                    connection.TryGetProperty("connectionReferenceLogicalName", out var inner) &&
                    inner.ValueKind == JsonValueKind.String)
                    logicalName = inner.GetString();
                if (string.IsNullOrWhiteSpace(logicalName))
                    return (false, [], "A connector alias has no explicit Dataverse connection-reference logical name.");
                names.Add(logicalName);
            }
            return (true, names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (JsonException)
        {
            // Never expose raw definitions: they can contain embedded configuration.
            return (false, [], "Flow dependency definition is not valid JSON.");
        }
    }

    /// <summary>
    /// Extracts connection reference logical names from flow client data JSON.
    /// </summary>
    /// <param name="clientData">The raw client data JSON from the workflow entity.</param>
    /// <returns>List of connection reference logical names (case-preserved).</returns>
    /// <remarks>
    /// Client data structure varies but connection references typically appear in:
    /// - properties.connectionReferences (object with CR logical names as keys)
    /// - definition.connectionReferences (alternative location)
    /// </remarks>
    public static List<string> ExtractConnectionReferenceLogicalNames(string? clientData)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(clientData))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(clientData);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            // Try properties.connectionReferences first
            if (root.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("connectionReferences", out var connectionRefs))
            {
                ExtractKeysFromObject(connectionRefs, result);
            }
            // Try top-level connectionReferences
            else if (root.TryGetProperty("connectionReferences", out var topLevelRefs))
            {
                ExtractKeysFromObject(topLevelRefs, result);
            }
            // Try definition.connectionReferences
            else if (root.TryGetProperty("definition", out var definition) &&
                     definition.ValueKind == JsonValueKind.Object &&
                     definition.TryGetProperty("connectionReferences", out var defRefs))
            {
                ExtractKeysFromObject(defRefs, result);
            }
        }
        catch (JsonException)
        {
            // Invalid JSON - return empty list
        }

        return result;
    }

    /// <summary>
    /// Extracts all keys from a JSON object (connection reference logical names).
    /// </summary>
    private static void ExtractKeysFromObject(JsonElement element, List<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.IsNullOrEmpty(property.Name))
            {
                keys.Add(property.Name);
            }
        }
    }
}
