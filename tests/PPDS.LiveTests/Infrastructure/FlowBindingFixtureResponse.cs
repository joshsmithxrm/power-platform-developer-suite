using System.Text.Json;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Validates the binding fixture's CLI response without changing its live safety checks.</summary>
internal static class FlowBindingFixtureResponse
{
    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Expected a CLI JSON response object.");
        return document.RootElement.Clone();
    }

    internal static void RequireUnboundConnectionId(JsonElement reference)
    {
        if (!reference.TryGetProperty("connectionId", out var connection) ||
            connection.ValueKind == JsonValueKind.Null ||
            (connection.ValueKind == JsonValueKind.String && connection.GetString() == string.Empty))
            return;

        throw new InvalidOperationException("Expected an omitted, null or empty connection ID for the unbound fixture reference.");
    }
}
