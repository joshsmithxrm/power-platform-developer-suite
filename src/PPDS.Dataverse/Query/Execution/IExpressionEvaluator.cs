using System.Collections.Generic;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Evaluates SQL expressions against a row of data.
/// Used by client-side plan nodes (Filter, Project, Aggregate, Sort).
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Optional variable scope for resolving @variable references.</summary>
    VariableScope? VariableScope { get; set; }

    /// <summary>Evaluate an expression, returning the computed value.</summary>
    object? Evaluate(ISqlExpression expression, IReadOnlyDictionary<string, QueryValue> row);

    /// <summary>Evaluate a condition, returning true/false.</summary>
    bool EvaluateCondition(ISqlCondition condition, IReadOnlyDictionary<string, QueryValue> row);
}
