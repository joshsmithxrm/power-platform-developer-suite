using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// SET @variable = expression.
/// Assigns a value to a previously declared SQL variable.
/// </summary>
public sealed class SqlSetVariableStatement : ISqlStatement
{
    /// <summary>The variable name including the @ prefix.</summary>
    public string VariableName { get; }

    /// <summary>The expression to assign.</summary>
    public ISqlExpression Value { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlSetVariableStatement"/> class.</summary>
    public SqlSetVariableStatement(string variableName, ISqlExpression value, int sourcePosition)
    {
        VariableName = variableName ?? throw new ArgumentNullException(nameof(variableName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        SourcePosition = sourcePosition;
    }
}
