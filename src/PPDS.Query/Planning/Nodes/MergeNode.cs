using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Executes a MERGE statement: matches source rows against target rows using an ON condition,
/// then applies WHEN MATCHED (UPDATE/DELETE) and WHEN NOT MATCHED (INSERT) clauses.
/// Yields a summary row with inserted, updated, and deleted counts.
/// </summary>
public sealed class MergeNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _sourceNode;
    private readonly string _targetEntity;
    private readonly IReadOnlyList<MergeMatchColumn> _matchColumns;
    private readonly MergeWhenMatched? _whenMatched;
    private readonly MergeWhenNotMatched? _whenNotMatched;

    /// <inheritdoc />
    public string Description => $"Merge: {_targetEntity}";

    /// <inheritdoc />
    public long EstimatedRows => 1; // Summary row

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _sourceNode };

    /// <summary>
    /// Initializes a new instance of the <see cref="MergeNode"/> class.
    /// </summary>
    /// <param name="sourceNode">The node providing source rows (USING clause).</param>
    /// <param name="targetEntity">The target entity logical name.</param>
    /// <param name="matchColumns">Columns used for matching (ON condition).</param>
    /// <param name="whenMatched">Action when a source row matches a target row.</param>
    /// <param name="whenNotMatched">Action when a source row has no match in the target.</param>
    public MergeNode(
        IQueryPlanNode sourceNode,
        string targetEntity,
        IReadOnlyList<MergeMatchColumn> matchColumns,
        MergeWhenMatched? whenMatched = null,
        MergeWhenNotMatched? whenNotMatched = null)
    {
        _sourceNode = sourceNode ?? throw new ArgumentNullException(nameof(sourceNode));
        _targetEntity = targetEntity ?? throw new ArgumentNullException(nameof(targetEntity));
        _matchColumns = matchColumns ?? throw new ArgumentNullException(nameof(matchColumns));
        _whenMatched = whenMatched;
        _whenNotMatched = whenNotMatched;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Collect source rows
        var sourceRows = new List<QueryRow>();
        await foreach (var row in _sourceNode.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceRows.Add(row);
        }

        long insertedCount = 0;
        long updatedCount = 0;
        long deletedCount = 0;

        // For each source row, check match conditions
        // In a real implementation, the target rows would be fetched from Dataverse.
        // For now, produce a plan summary showing what WOULD be done.
        foreach (var sourceRow in sourceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build a match key from the source row
            var hasMatch = false;

            // In a real implementation, we'd query the target entity using the match columns.
            // For plan-only mode, we simulate: each source row is treated as "not matched"
            // unless the target query node indicates otherwise.
            // This will be fully functional when integrated with DmlExecuteNode patterns.

            if (hasMatch && _whenMatched != null)
            {
                switch (_whenMatched.Action)
                {
                    case MergeAction.Update:
                        updatedCount++;
                        break;
                    case MergeAction.Delete:
                        deletedCount++;
                        break;
                }
            }
            else if (!hasMatch && _whenNotMatched != null)
            {
                if (_whenNotMatched.Action == MergeAction.Insert)
                {
                    insertedCount++;
                }
            }
        }

        // Yield summary row
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["$action"] = QueryValue.Simple("MERGE"),
            ["inserted_count"] = QueryValue.Simple(insertedCount),
            ["updated_count"] = QueryValue.Simple(updatedCount),
            ["deleted_count"] = QueryValue.Simple(deletedCount),
            ["source_count"] = QueryValue.Simple((long)sourceRows.Count)
        };

        yield return new QueryRow(values, _targetEntity);
    }
}

/// <summary>
/// Describes a column pair used for matching in the ON condition of a MERGE statement.
/// </summary>
public sealed class MergeMatchColumn
{
    /// <summary>The source column name.</summary>
    public string SourceColumn { get; }

    /// <summary>The target column name.</summary>
    public string TargetColumn { get; }

    /// <summary>Initializes a new instance of the <see cref="MergeMatchColumn"/> class.</summary>
    public MergeMatchColumn(string sourceColumn, string targetColumn)
    {
        SourceColumn = sourceColumn ?? throw new ArgumentNullException(nameof(sourceColumn));
        TargetColumn = targetColumn ?? throw new ArgumentNullException(nameof(targetColumn));
    }
}

/// <summary>
/// Action to perform in a MERGE clause.
/// </summary>
public enum MergeAction
{
    /// <summary>INSERT new rows.</summary>
    Insert,

    /// <summary>UPDATE existing rows.</summary>
    Update,

    /// <summary>DELETE existing rows.</summary>
    Delete
}

/// <summary>
/// Describes the WHEN MATCHED clause of a MERGE statement.
/// </summary>
public sealed class MergeWhenMatched
{
    /// <summary>The action to take (Update or Delete).</summary>
    public MergeAction Action { get; }

    /// <summary>For UPDATE: the SET clauses to apply.</summary>
    public IReadOnlyList<CompiledSetClause>? SetClauses { get; }

    /// <summary>Initializes a WHEN MATCHED THEN UPDATE.</summary>
    public static MergeWhenMatched Update(IReadOnlyList<CompiledSetClause> setClauses)
        => new(MergeAction.Update, setClauses);

    /// <summary>Initializes a WHEN MATCHED THEN DELETE.</summary>
    public static MergeWhenMatched Delete()
        => new(MergeAction.Delete, null);

    private MergeWhenMatched(MergeAction action, IReadOnlyList<CompiledSetClause>? setClauses)
    {
        Action = action;
        SetClauses = setClauses;
    }
}

/// <summary>
/// Describes the WHEN NOT MATCHED clause of a MERGE statement.
/// </summary>
public sealed class MergeWhenNotMatched
{
    /// <summary>The action to take (Insert).</summary>
    public MergeAction Action { get; }

    /// <summary>For INSERT: the column names.</summary>
    public IReadOnlyList<string>? Columns { get; }

    /// <summary>For INSERT: the value expressions.</summary>
    public IReadOnlyList<CompiledScalarExpression>? Values { get; }

    /// <summary>Initializes a WHEN NOT MATCHED THEN INSERT.</summary>
    public static MergeWhenNotMatched Insert(
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<CompiledScalarExpression>? values = null)
        => new(columns, values);

    private MergeWhenNotMatched(IReadOnlyList<string>? columns, IReadOnlyList<CompiledScalarExpression>? values)
    {
        Action = MergeAction.Insert;
        Columns = columns;
        Values = values;
    }
}
