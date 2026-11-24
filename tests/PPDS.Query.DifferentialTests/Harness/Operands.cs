namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>What an operand slot of a rule accepts and what a rule or leaf produces.</summary>
public enum Kind
{
    /// <summary>A number: int, bigint, decimal, money or float.</summary>
    Number,
    /// <summary>Text: varchar or nvarchar.</summary>
    Text,
    /// <summary>A predicate (a comparison); only an operand of IIF and CASE.</summary>
    Predicate,
}

/// <summary>
/// The operand table: typed columns, one row per set of boundary values. The harness creates it in its own database
/// on SQL Server, reads the stored rows back, and gives the engine under test the same values as .NET values, the way
/// the engine receives a Dataverse row (a currency value is a plain decimal once read, a whole number an int, and so
/// on). Both engines therefore start from the same operands, converted by SQL Server itself.
/// </summary>
/// <remarks>
/// The column types are the harness's choice. Which SQL type each Dataverse column kind has is established by the
/// read-only endpoint probe (#1799); when that is known, these columns follow it.
/// </remarks>
public static class Operands
{
    /// <summary>A typed column of the operand table.</summary>
    public sealed record Column(string Name, string SqlType, Kind Kind);

    /// <summary>A row of the operand table: an id, what it is for, and a SQL literal per column, in column order.</summary>
    public sealed record Row(int Id, string Purpose, string[] Literals);

    /// <summary>The columns, in table order.</summary>
    public static readonly IReadOnlyList<Column> Columns =
    [
        new("c_int", "int", Kind.Number),
        new("c_bigint", "bigint", Kind.Number),
        new("c_dec", "decimal(23,10)", Kind.Number),
        new("c_dec2", "decimal(12,2)", Kind.Number),
        new("c_money", "money", Kind.Number),
        new("c_float", "float", Kind.Number),
        new("c_ntext", "nvarchar(100)", Kind.Text),
        new("c_text", "varchar(100)", Kind.Text),
    ];

    /// <summary>The rows. Every generated expression is evaluated over each of them.</summary>
    public static readonly IReadOnlyList<Row> Rows =
    [
        new(1, "ordinary values", ["7", "3000000000", "2.5", "2.50", "12.3456", "0.1", "N'Abc'", "'abc '"]),
        new(2, "zero and empty", ["0", "0", "0", "0.00", "0", "0", "N''", "''"]),
        new(3, "negative and padded", ["-7", "-9000000000", "-1.0000000001", "-0.01", "-0.0001", "-2.5", "N' x '", "' x'"]),
        new(4, "upper bounds", ["2147483647", "9223372036854775807", "9999999999999.9999999999", "9999999999.99", "922337203685477.5807", "1.7976931348623157E308", "N'\u00DFtra\u00DFe'", "'Z'"]),
        new(5, "lower bounds", ["-2147483648", "-9223372036854775808", "-9999999999999.9999999999", "-9999999999.99", "-922337203685477.5808", "-1.7976931348623157E308", "N'\u0101b'", "'~'"]),
        new(6, "all NULL", ["NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL"]),
        new(7, "line breaks and small fractions", ["1", "2147483648", "0.0000000001", "0.01", "0.0001", "1E-300", "N'a' + NCHAR(10) + N'b'", "'xa' + CHAR(10)"]),
    ];

    /// <summary>The ids of every row.</summary>
    public static IReadOnlyList<int> RowIds { get; } = Rows.Select(r => r.Id).ToArray();

    /// <summary>The statement that creates the operand table.</summary>
    public static string CreateTableSql()
        => "CREATE TABLE dbo.Operands (row_id int NOT NULL PRIMARY KEY, "
           + string.Join(", ", Columns.Select(c => $"{c.Name} {c.SqlType} NULL")) + ");";

    /// <summary>The statement that inserts every row.</summary>
    public static string InsertRowsSql()
        => "INSERT INTO dbo.Operands (row_id, " + string.Join(", ", Columns.Select(c => c.Name)) + ") VALUES "
           + string.Join(", ", Rows.Select(r => $"({r.Id}, {string.Join(", ", r.Literals)})")) + ";";
}
