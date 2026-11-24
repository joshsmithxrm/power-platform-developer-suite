using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>Evaluates a scalar expression over one operand row.</summary>
public interface IExpressionEngine
{
    /// <summary>The outcome of <c>SELECT <paramref name="expression"/></c> over the operand row <paramref name="rowId"/>.</summary>
    Outcome Evaluate(string expression, int rowId);
}

/// <summary>
/// The reference: SQL Server. The value is read with SqlClient's <see cref="System.Data.SqlTypes"/> values, so that a
/// decimal keeps its scale and a 38-digit value is not cut to .NET's 28. The result type comes from
/// <c>sp_describe_first_result_set</c>, and is checked against the column metadata of the executed result set.
/// </summary>
/// <remarks>
/// <para>
/// Why <c>sp_describe_first_result_set</c>: it returns the type SQL Server's own binder gives the result column, with
/// its precision, scale and length (<c>system_type_name</c>, such as <c>numeric(16,6)</c> or <c>varchar(4)</c>), as
/// documented on its page in the Transact-SQL reference. It analyses the statement without running it, so it also
/// types an expression whose evaluation fails (a division by zero still has a type). It is the same type the server
/// then sends in the result set's metadata; the harness checks that both agree on every successful evaluation and
/// stops with a harness error if they do not. The metadata alone is not enough: SqlClient reports no scale for
/// money and no length facet beyond the column size, and there is no metadata when evaluation fails.
/// </para>
/// <para>
/// <c>SQL_VARIANT_PROPERTY</c> was not used: it needs a conversion to <c>sql_variant</c>, which changes nothing for
/// most types but fails for <c>max</c> types, and it reports nothing for a NULL.
/// </para>
/// </remarks>
public sealed class SqlServerEngine(SqlConnection connection) : IExpressionEngine
{
    private readonly Dictionary<string, (SqlTypeInfo? Type, Outcome? Error)> _typeCache = new(StringComparer.Ordinal);

    /// <summary>How many statements were sent to the server.</summary>
    public int RoundTrips { get; private set; }

    /// <summary>The statement the harness runs for an expression over a row.</summary>
    public static string StatementFor(string expression, int rowId)
        => $"SELECT {expression} AS v FROM dbo.Operands WHERE row_id = {rowId}";

    /// <inheritdoc />
    public Outcome Evaluate(string expression, int rowId)
    {
        // The type does not depend on the row; describe once per expression. A failure to describe is a compile-time
        // error (a type clash), and it is the outcome for every row.
        var described = Describe(expression, out var compileError);
        if (compileError is not null)
            return compileError;

        try
        {
            RoundTrips++;
            using var command = new SqlCommand(StatementFor(expression, rowId), connection);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new HarnessException($"Operand row {rowId} is missing.");
            var value = reader.GetSqlValue(0);
            CheckMetadataAgrees(expression, described!, reader.GetColumnSchema()[0]);
            return Outcome.Of(Outcome.Render(value), described, described!.Family);
        }
        catch (SqlException ex)
        {
            return Classify(expression, ex);
        }
    }

    private SqlTypeInfo? Describe(string expression, out Outcome? compileError)
    {
        if (_typeCache.TryGetValue(expression, out var cached))
        {
            compileError = cached.Error;
            return cached.Type;
        }

        try
        {
            RoundTrips++;
            using var command = new SqlCommand("EXEC sys.sp_describe_first_result_set @tsql", connection);
            command.Parameters.Add(new SqlParameter("@tsql", SqlDbType.NVarChar, -1) { Value = StatementFor(expression, 1) });
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new HarnessException($"sp_describe_first_result_set returned no column for: {expression}");
            cached = (SqlTypeInfo.Parse(
                (string)reader["system_type_name"],
                Convert.ToInt32(reader["precision"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["scale"], CultureInfo.InvariantCulture)), null);
        }
        catch (SqlException ex)
        {
            // sp_describe_first_result_set re-raises the compile error of the statement it describes (wrapped in its
            // own error 11514 or 11529; the statement's own number is kept in the collection).
            cached = (null, Classify(expression, ex));
        }
        _typeCache[expression] = cached;
        compileError = cached.Error;
        return cached.Type;
    }

    private static void CheckMetadataAgrees(string expression, SqlTypeInfo described, DbColumn column)
    {
        var name = column.DataTypeName?.ToLowerInvariant();
        var agrees = name == described.Name
                     && (described.Name != "decimal" || (column.NumericPrecision == described.Precision && column.NumericScale == described.Scale))
                     && (described.Length is null || described.Length == -1 || column.ColumnSize == described.Length);
        if (!agrees)
        {
            throw new HarnessException(
                $"The two type facilities disagree for {expression}: sp_describe_first_result_set says {described}, the result set says "
                + $"{column.DataTypeName} precision {column.NumericPrecision} scale {column.NumericScale} size {column.ColumnSize}.");
        }
    }

    /// <summary>Maps a SQL Server error to its class. A syntax error means the generator wrote bad SQL: a harness error.</summary>
    public static Outcome Classify(string expression, SqlException ex)
    {
        var numbers = ex.Errors.Cast<SqlError>().Select(e => e.Number).Where(n => n is not 11514 and not 11529 and not 11501).ToList();
        var number = numbers.Count > 0 ? numbers[0] : ex.Number;
        var errorClass = number switch
        {
            8134 => ErrorClass.DivideByZero,
            220 or 232 or 237 or 3606 or 8115 => ErrorClass.Overflow,
            235 or 241 or 245 or 248 or 293 or 295 or 8114 or 8169 => ErrorClass.Conversion,
            2628 or 8152 => ErrorClass.Truncation,
            206 or 402 or 529 or 4151 or 8116 or 8117 => ErrorClass.TypeClash,
            102 or 156 or 174 or 189 or 195 or 207 or 208 or 4145 =>
                throw new HarnessException($"SQL Server rejected the harness's SQL (error {number}): {expression}: {ReferenceServer.Redact(ex.Message)}"),
            _ => ErrorClass.Other,
        };
        return Outcome.Failed(errorClass, number.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The engine under test: PPDS's <see cref="ExpressionCompiler"/>, which computes every scalar value the query engine
/// evaluates itself: a SELECT without FROM, computed columns, client-side filters, and the values DML writes. The
/// expression is parsed by the product's <see cref="QueryParser"/> as <c>SELECT expression</c> and compiled and run
/// over the operand row exactly as a plan node does.
/// </summary>
/// <remarks>
/// Today's engine has no SQL types: it picks each operation from the .NET type of the values it receives. Its result
/// type is therefore reported as a family, read from the .NET type of the value (int, bigint, decimal, float, text,
/// bit), with no precision, scale or length, and none at all for NULL. The value comparison still includes the scale,
/// because a .NET decimal carries one. When the typed engine (#1800 onward) reports full SQL types, its adapter sets
/// <see cref="Outcome.Type"/> and the comparison becomes exact.
/// </remarks>
public sealed class PpdsEngine(IReadOnlyDictionary<int, IReadOnlyDictionary<string, QueryValue>> rows) : IExpressionEngine
{
    private readonly QueryParser _parser = new();
    private readonly ExpressionCompiler _compiler = new();
    private readonly Dictionary<string, (CompiledScalarExpression? Compiled, Outcome? Refusal)> _compiled = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Outcome Evaluate(string expression, int rowId)
    {
        var (compiled, refusal) = Compile(expression);
        if (refusal is not null)
            return refusal;

        object? value;
        try
        {
            value = compiled!(rows[rowId]);
        }
        catch (Exception ex)
        {
            return Outcome.Failed(Classify(ex), ex.GetType().Name + (ex is QueryExecutionException q ? " " + q.ErrorCode : ""));
        }
        return Outcome.Of(Outcome.Render(value), null, FamilyOf(value));
    }

    private (CompiledScalarExpression?, Outcome?) Compile(string expression)
    {
        if (_compiled.TryGetValue(expression, out var cached))
            return cached;
        try
        {
            var statement = (SelectStatement)_parser.ParseStatement("SELECT " + expression);
            var element = (SelectScalarExpression)((QuerySpecification)statement.QueryExpression).SelectElements[0];
            cached = (_compiler.CompileScalar(element.Expression), null);
        }
        catch (Exception ex)
        {
            cached = (null, Outcome.Failed(ErrorClass.Refused, ex.GetType().Name));
        }
        _compiled[expression] = cached;
        return cached;
    }

    private static ErrorClass Classify(Exception ex) => ex switch
    {
        DivideByZeroException => ErrorClass.DivideByZero,
        OverflowException => ErrorClass.Overflow,
        FormatException or InvalidCastException => ErrorClass.Conversion,
        QueryExecutionException { ErrorCode: QueryErrorCode.TypeMismatch } => ErrorClass.TypeClash,
        NotSupportedException => ErrorClass.Refused,
        _ => ErrorClass.Other,
    };

    /// <summary>The type family of a value computed by today's engine; null for NULL, which carries no type.</summary>
    public static string? FamilyOf(object? value) => value switch
    {
        null => null,
        int or short or byte => "int",
        long => "bigint",
        decimal => "decimal",
        double or float => "float",
        string => "text",
        bool => "bit",
        DateTime => "datetime",
        Guid => "uniqueidentifier",
        _ => value.GetType().Name,
    };
}

/// <summary>A fault of the harness itself, not a difference between the engines.</summary>
public sealed class HarnessException(string message) : Exception(message);
