namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// DECLARE @name TYPE [= value].
/// Declares a SQL variable with a type and optional initial value.
/// </summary>
public sealed class SqlDeclareStatement : ISqlStatement
{
    /// <summary>The variable name including the @ prefix.</summary>
    public string VariableName { get; }

    /// <summary>The declared type name (e.g., "MONEY", "NVARCHAR(100)").</summary>
    public string TypeName { get; }

    /// <summary>Optional initial value expression.</summary>
    public ISqlExpression? InitialValue { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlDeclareStatement"/> class.</summary>
    public SqlDeclareStatement(string variableName, string typeName, ISqlExpression? initialValue, int sourcePosition)
    {
        VariableName = variableName;
        TypeName = typeName;
        InitialValue = initialValue;
        SourcePosition = sourcePosition;
    }
}
