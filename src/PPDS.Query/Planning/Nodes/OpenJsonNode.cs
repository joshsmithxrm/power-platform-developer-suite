using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Plan node for OPENJSON(json_expression [, path]).
/// Shreds a JSON string into rows with key, value, and type columns.
/// </summary>
public sealed class OpenJsonNode : IQueryPlanNode
{
    private readonly CompiledScalarExpression _jsonExpression;
    private readonly string? _path;

    /// <inheritdoc />
    public string Description => _path != null ? $"OpenJson: {_path}" : "OpenJson";

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="OpenJsonNode"/> class.</summary>
    public OpenJsonNode(CompiledScalarExpression jsonExpression, string? path = null)
    {
        _jsonExpression = jsonExpression ?? throw new ArgumentNullException(nameof(jsonExpression));
        _path = path;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var jsonValue = _jsonExpression(new Dictionary<string, QueryValue>());
        if (jsonValue is null) yield break;

        var jsonString = jsonValue.ToString();
        if (string.IsNullOrEmpty(jsonString)) yield break;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        // Navigate to path if specified
        var target = root;
        if (_path != null)
        {
            target = NavigatePath(root, _path);
            if (target.ValueKind == JsonValueKind.Undefined) yield break;
        }

        if (target.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in target.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MakeRow(index.ToString(), element);
                index++;
            }
        }
        else if (target.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in target.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MakeRow(property.Name, property.Value);
            }
        }
    }

    private static QueryRow MakeRow(string key, JsonElement element)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = QueryValue.Simple(key),
            ["value"] = QueryValue.Simple(GetStringValue(element)),
            ["type"] = QueryValue.Simple(GetJsonType(element))
        };
        return new QueryRow(values, "openjson");
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// Returns the OPENJSON type code: 0=null, 1=string, 2=number, 3=boolean, 4=array, 5=object.
    /// </summary>
    private static int GetJsonType(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.String => 1,
            JsonValueKind.Number => 2,
            JsonValueKind.True or JsonValueKind.False => 3,
            JsonValueKind.Array => 4,
            JsonValueKind.Object => 5,
            _ => 0
        };
    }

    private static JsonElement NavigatePath(JsonElement root, string path)
    {
        var current = root;
        var segments = path.TrimStart('$', '.').Split('.');

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;

            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else
            {
                return default; // Undefined
            }
        }

        return current;
    }
}
