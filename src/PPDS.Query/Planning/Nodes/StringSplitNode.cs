using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements the STRING_SPLIT table-valued function.
/// <c>SELECT value FROM STRING_SPLIT('red,green,blue', ',')</c>
/// Splits an input string by a separator and yields one row per value with a "value" column.
/// </summary>
public sealed class StringSplitNode : IQueryPlanNode
{
    private readonly string _inputString;
    private readonly string _separator;
    private readonly bool _enableOrdinal;

    /// <inheritdoc />
    public string Description => $"StringSplit: '{Truncate(_inputString, 30)}' by '{_separator}'";

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="StringSplitNode"/> class.
    /// </summary>
    /// <param name="inputString">The string to split.</param>
    /// <param name="separator">The separator to split by.</param>
    /// <param name="enableOrdinal">
    /// When true, includes an "ordinal" column with 1-based position of each value.
    /// Corresponds to STRING_SPLIT's optional enable_ordinal parameter.
    /// </param>
    public StringSplitNode(string inputString, string separator, bool enableOrdinal = false)
    {
        _inputString = inputString ?? throw new ArgumentNullException(nameof(inputString));
        _separator = separator ?? throw new ArgumentNullException(nameof(separator));
        _enableOrdinal = enableOrdinal;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_inputString))
        {
            yield break;
        }

        string[] parts;
        if (string.IsNullOrEmpty(_separator))
        {
            // Empty separator: each character becomes a value
            parts = new string[_inputString.Length];
            for (int i = 0; i < _inputString.Length; i++)
            {
                parts[i] = _inputString[i].ToString();
            }
        }
        else
        {
            parts = _inputString.Split(new[] { _separator }, StringSplitOptions.None);
        }

        for (int i = 0; i < parts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = QueryValue.Simple(parts[i])
            };

            if (_enableOrdinal)
            {
                values["ordinal"] = QueryValue.Simple(i + 1);
            }

            yield return new QueryRow(values, "string_split");
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value.Substring(0, maxLength) + "...";
    }
}
