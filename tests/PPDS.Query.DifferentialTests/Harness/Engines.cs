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
/// decimal keeps its scale and a 38-digit value is not cut to .NET's 28. The result type is the type of the result
/// column the server sends with the value (the TDS column metadata, read through
/// <see cref="Microsoft.Data.SqlClient.SqlDataReader.GetColumnSchema"/>).
/// </summary>
/// <remarks>
/// <para>
/// Why the result set's own metadata: it is, by definition, the type of the value SQL Server returned, with the
/// precision and scale of a decimal and the length of a character type. The only facet it leaves out is the scale of
/// money and smallmoney, which their documentation fixes at 4 (precision 19 and 10).
/// </para>
/// <para>
/// <c>sp_describe_first_result_set</c> was tried first and rejected: it types the statement without running it and can
/// disagree with the executed result. For <c>CASE WHEN CAST(1.0000000000000002 AS float) &gt; 1.0 THEN 'gt' ELSE 'not' END</c>
/// it reports <c>varchar(2)</c>, the type of the branch it folds to, while the executed result is <c>varchar(3)</c>.
/// <c>SQL_VARIANT_PROPERTY</c> was not used either: it needs a conversion to <c>sql_variant</c>, which fails for
/// <c>max</c> types and reports nothing for a NULL. When evaluation fails there is no metadata, and none is needed:
/// the type is not compared when SQL Server raises an error.
/// </para>
/// </remarks>
public sealed class SqlServerEngine(SqlConnection connection) : IExpressionEngine
{
    /// <summary>How many statements were sent to the server.</summary>
    public int RoundTrips { get; private set; }

    /// <summary>The statement the harness runs for an expression over a row.</summary>
    public static string StatementFor(string expression, int rowId)
        => $"SELECT {expression} AS v FROM dbo.Operands WHERE row_id = {rowId}";

    /// <inheritdoc />
    public Outcome Evaluate(string expression, int rowId)
    {
        try
        {
            RoundTrips++;
            using var command = new SqlCommand(StatementFor(expression, rowId), connection);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new HarnessException($"Operand row {rowId} is missing.");
            var value = reader.GetSqlValue(0);
            var type = TypeOf(reader.GetColumnSchema()[0]);
            return Outcome.Of(Outcome.Render(value), type, type.Family);
        }
        catch (SqlException ex)
        {
            ThrowIfHarnessFault(connection, expression, ex);
            return Classify(expression, ex);
        }
    }

    /// <summary>
    /// Throws a <see cref="HarnessException"/> when the error is about the connection rather than the expression: a
    /// timeout (-2), a severity of 20 or more (the connection is closed), or a connection left not open. Such an error
    /// is not an outcome to compare.
    /// </summary>
    public static void ThrowIfHarnessFault(SqlConnection connection, string statement, SqlException ex)
    {
        if (ex.Number == -2 || ex.Class >= 20 || connection.State != System.Data.ConnectionState.Open)
        {
            throw new HarnessException(
                $"The reference server failed rather than the statement (error {ex.Number}, severity {ex.Class}): {statement}: {ReferenceServer.Redact(ex.Message)}");
        }
    }

    /// <summary>The SQL type of a result column, from its metadata.</summary>
    public static SqlTypeInfo TypeOf(DbColumn column)
        => SqlTypeInfo.FromMetadata(column.DataTypeName ?? "?", column.NumericPrecision, column.NumericScale, column.ColumnSize);

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
            206 or 402 or 529 or 4127 or 4151 or 8116 or 8117 or 8133 => ErrorClass.TypeClash,
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
        // A missing row is a fault of the harness, not an error of the engine: look it up outside the engine's try.
        if (!rows.TryGetValue(rowId, out var row))
            throw new HarnessException($"Operand row {rowId} is missing.");

        var (compiled, refusal) = Compile(expression);
        if (refusal is not null)
            return refusal;

        object? value;
        try
        {
            value = compiled!(row);
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
        int => "int",
        short => "smallint",
        byte => "tinyint",
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
