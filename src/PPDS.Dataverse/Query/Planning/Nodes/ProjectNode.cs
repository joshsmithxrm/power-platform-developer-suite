using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Maps input rows to output rows (column selection, renaming, expression evaluation).
/// </summary>
public sealed class ProjectNode : IQueryPlanNode
{
    /// <summary>The input node providing source rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <summary>The output column projections.</summary>
    public IReadOnlyList<ProjectColumn> OutputColumns { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var cols = new List<string>();
            foreach (var c in OutputColumns)
            {
                cols.Add(c.OutputName);
            }
            return $"Project: [{string.Join(", ", cols)}]";
        }
    }

    /// <inheritdoc />
    public long EstimatedRows => Input.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="ProjectNode"/> class.</summary>
    public ProjectNode(IQueryPlanNode input, IReadOnlyList<ProjectColumn> outputColumns)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        OutputColumns = outputColumns ?? throw new ArgumentNullException(nameof(outputColumns));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var inputRow in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outputValues = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in OutputColumns)
            {
                if (col.Expression != null)
                {
                    // Evaluate computed column via compiled delegate
                    var value = col.Expression(inputRow.Values);
                    outputValues[col.OutputName] = QueryValue.Simple(value);
                }
                else if (col.SourceName != null)
                {
                    // Simple rename/copy
                    if (inputRow.Values.TryGetValue(col.SourceName, out var qv))
                    {
                        outputValues[col.OutputName] = qv;
                    }
                    else
                    {
                        // Case-insensitive fallback
                        var found = false;
                        foreach (var kvp in inputRow.Values)
                        {
                            if (string.Equals(kvp.Key, col.SourceName, StringComparison.OrdinalIgnoreCase))
                            {
                                outputValues[col.OutputName] = kvp.Value;
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            outputValues[col.OutputName] = QueryValue.Null;
                        }
                    }
                }
                else
                {
                    outputValues[col.OutputName] = QueryValue.Null;
                }
            }

            yield return new QueryRow(outputValues, inputRow.EntityLogicalName);
        }
    }
}

/// <summary>
/// Defines a column in a ProjectNode's output.
/// </summary>
public sealed class ProjectColumn
{
    /// <summary>Source column name to copy from input row. Null if using Expression.</summary>
    public string? SourceName { get; }

    /// <summary>Output column name in the result row.</summary>
    public string OutputName { get; }

    /// <summary>Compiled expression to evaluate for computed columns. Null for simple copy/rename.</summary>
    public CompiledScalarExpression? Expression { get; }

    private ProjectColumn(string? sourceName, string outputName, CompiledScalarExpression? expression)
    {
        SourceName = sourceName;
        OutputName = outputName ?? throw new ArgumentNullException(nameof(outputName));
        Expression = expression;
    }

    /// <summary>Creates a pass-through column (same name in and out).</summary>
    public static ProjectColumn PassThrough(string name) => new(name, name, null);

    /// <summary>Creates a renamed column.</summary>
    public static ProjectColumn Rename(string sourceName, string outputName) => new(sourceName, outputName, null);

    /// <summary>Creates a computed column from a compiled expression.</summary>
    public static ProjectColumn Computed(string outputName, CompiledScalarExpression expression) => new(null, outputName, expression);
}
