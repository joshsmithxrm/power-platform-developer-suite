using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Filters rows by evaluating a condition client-side.
/// Used for HAVING clauses and expressions that can't be pushed to FetchXML.
/// </summary>
public sealed class ClientFilterNode : IQueryPlanNode
{
    /// <summary>The child node that produces input rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <summary>The condition to evaluate against each row.</summary>
    public ISqlCondition Condition { get; }

    /// <inheritdoc />
    public string Description => $"ClientFilter: {ConditionDescription()}";

    /// <inheritdoc />
    public long EstimatedRows => Input.EstimatedRows; // Conservative estimate

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="ClientFilterNode"/> class.</summary>
    public ClientFilterNode(IQueryPlanNode input, ISqlCondition condition)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context.ExpressionEvaluator.EvaluateCondition(Condition, row.Values))
            {
                yield return row;
            }
        }
    }

    private string ConditionDescription()
    {
        return Condition switch
        {
            SqlComparisonCondition comp => $"{comp.Column.GetFullName()} {comp.Operator} {comp.Value.Value}",
            SqlExpressionCondition expr => $"expr {expr.Operator} expr",
            SqlLogicalCondition logical => $"({logical.Operator} with {logical.Conditions.Count} conditions)",
            _ => Condition.GetType().Name
        };
    }
}
