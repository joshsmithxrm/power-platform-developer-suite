using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PPDS.Dataverse.Services.Utilities;

/// <summary>
/// Parses flow client data to extract connection reference logical names.
/// </summary>
public static class FlowClientDataParser
{
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

            // Try properties.connectionReferences first
            if (root.TryGetProperty("properties", out var properties) &&
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
