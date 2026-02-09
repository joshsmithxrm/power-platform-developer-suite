using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Manages SQL variable declarations and values.
/// Variables are declared with DECLARE @name TYPE [= value]
/// and used with SET @name = expression or in WHERE clauses.
/// </summary>
public sealed class VariableScope
{
    private readonly Dictionary<string, VariableInfo> _variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Declares a variable with a type and optional initial value.
    /// </summary>
    /// <param name="name">Variable name (must start with @).</param>
    /// <param name="typeName">The SQL type name.</param>
    /// <param name="initialValue">Optional initial value.</param>
    public void Declare(string name, string typeName, object? initialValue = null)
    {
        if (!name.StartsWith("@"))
            throw new ArgumentException("Variable names must start with @", nameof(name));
        _variables[name] = new VariableInfo(name, typeName, initialValue);
    }

    /// <summary>
    /// Sets the value of a previously declared variable.
    /// </summary>
    /// <param name="name">Variable name (must have been declared).</param>
    /// <param name="value">The new value.</param>
    public void Set(string name, object? value)
    {
        if (!_variables.TryGetValue(name, out var existing))
            throw new QueryExecutionException(
                QueryErrorCode.ExecutionFailed,
                $"Variable {name} has not been declared. Use DECLARE {name} <type> before SET.");
        _variables[name] = existing with { Value = value };
    }

    /// <summary>
    /// Gets the current value of a declared variable.
    /// </summary>
    /// <param name="name">Variable name.</param>
    /// <returns>The variable's current value.</returns>
    public object? Get(string name)
    {
        if (_variables.TryGetValue(name, out var info))
            return info.Value;
        throw new QueryExecutionException(
            QueryErrorCode.ExecutionFailed,
            $"Variable {name} has not been declared. Use DECLARE {name} <type> before referencing it.");
    }

    /// <summary>
    /// Checks whether a variable has been declared.
    /// </summary>
    public bool IsDeclared(string name) => _variables.ContainsKey(name);

    /// <summary>
    /// Gets all declared variables (read-only view).
    /// </summary>
    public IReadOnlyDictionary<string, VariableInfo> Variables => _variables;
}

/// <summary>
/// Information about a declared SQL variable.
/// </summary>
/// <param name="Name">The variable name including @ prefix.</param>
/// <param name="TypeName">The declared SQL type name.</param>
/// <param name="Value">The current value (null if not yet assigned).</param>
public sealed record VariableInfo(string Name, string TypeName, object? Value);
