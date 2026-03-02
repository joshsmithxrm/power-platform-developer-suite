using System;
using System.Data;
using System.Data.Common;

namespace PPDS.Query.Provider;

/// <summary>
/// A parameter for a <see cref="PpdsDbCommand"/>. Values are substituted
/// into the SQL text before parsing and execution.
/// </summary>
public sealed class PpdsDbParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private DbType _dbType = DbType.String;
    private bool _dbTypeSet;

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbParameter"/> class.
    /// </summary>
    public PpdsDbParameter()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbParameter"/> class
    /// with a parameter name and value.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="value">The value of the parameter.</param>
    public PpdsDbParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <inheritdoc />
    public override string ParameterName
    {
        get => _parameterName;
#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member
        set => _parameterName = value ?? string.Empty;
#pragma warning restore CS8765
    }

    /// <inheritdoc />
    public override object? Value { get; set; }

    /// <inheritdoc />
    public override DbType DbType
    {
        get
        {
            if (!_dbTypeSet && Value != null)
                return InferDbType(Value);
            return _dbType;
        }
        set
        {
            _dbType = value;
            _dbTypeSet = true;
        }
    }

    /// <inheritdoc />
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    /// <inheritdoc />
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member
    public override string SourceColumn { get; set; } = string.Empty;
#pragma warning restore CS8765

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    /// <inheritdoc />
    public override void ResetDbType()
    {
        _dbType = DbType.String;
        _dbTypeSet = false;
    }

    /// <summary>
    /// Returns the SQL literal representation of this parameter's value,
    /// suitable for substitution into the SQL text.
    /// </summary>
    internal string ToSqlLiteral()
    {
        if (Value is null || Value == DBNull.Value)
            return "NULL";

        return Value switch
        {
            string s => $"'{EscapeSqlString(s)}'",
            bool b => b ? "1" : "0",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss}'",
            Guid g => $"'{g}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
        };
    }

    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''");
    }

    private static DbType InferDbType(object value)
    {
        return value switch
        {
            string => DbType.String,
            int => DbType.Int32,
            long => DbType.Int64,
            short => DbType.Int16,
            byte => DbType.Byte,
            decimal => DbType.Decimal,
            double => DbType.Double,
            float => DbType.Single,
            bool => DbType.Boolean,
            DateTime => DbType.DateTime,
            DateTimeOffset => DbType.DateTimeOffset,
            Guid => DbType.Guid,
            byte[] => DbType.Binary,
            _ => DbType.String
        };
    }
}
