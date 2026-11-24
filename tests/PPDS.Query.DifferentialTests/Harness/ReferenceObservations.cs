namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// A behaviour of SQL Server that its documentation leaves silent or ambiguous, settled by running it on the reference
/// server (Decision F2: such a rule cites the differential case that establishes it). The SQL Server outcome is pinned;
/// a different server or setting fails the case. A scalar expression also runs through the engine, and the run reports
/// whether the engine agrees; a difference found here is a finding to file, not a pass or fail of this case.
/// </summary>
/// <param name="Id">A short stable id, with a letter per statement of the same question.</param>
/// <param name="Question">What it settles, and the Microsoft page that leaves it open.</param>
/// <param name="Sql">A scalar expression (evaluated over operand row 1), or a batch when <paramref name="IsBatch"/>.</param>
/// <param name="SqlServer">The pinned reference outcome: <see cref="Outcome.ToString"/> for an expression, <see cref="ReferenceBatch.Run"/> for a batch.</param>
/// <param name="IsBatch">True for a batch that only SQL Server runs.</param>
public sealed record ReferenceObservation(string Id, string Question, string Sql, string SqlServer, bool IsBatch = false)
{
    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>The observations.</summary>
public static class ReferenceObservations
{
    private static ReferenceObservation E(string id, string question, string sql, string sqlServer) => new(id, question, sql, sqlServer);

    private static ReferenceObservation B(string id, string question, string sql, string sqlServer) => new(id, question, sql, sqlServer, IsBatch: true);

    private const string Literals = "Type of a literal from its spelling (Constants)";
    private const string Nulls = "Type of the NULL constant; all-NULL IIF and COALESCE (IIF, COALESCE)";
    private const string Money = "Currency division and chaining; money with a decimal (money and smallmoney; Data type precedence)";
    private const string ToMoney = "Decimal and float to money; money to int (CAST and CONVERT, Truncating and rounding results)";
    private const string ToInt = "Decimal and float to int, and decimal to fewer places (CAST and CONVERT, Truncating and rounding results)";
    private const string MoneyToDecimal = "Money to decimal: CAST and CONVERT says round, the money page says truncate";
    private const string Casing = "UPPER and LOWER by collation (UPPER, LOWER)";
    private const string RoundOverflow = "ROUND with a positive or zero length: overflow or widen (ROUND)";
    private const string DateFormat = "yyyy-MM-dd to datetime under DATEFORMAT dmy and French (datetime; SET DATEFORMAT)";
    private const string Offset = "A date-time string with Z or an offset to datetime (datetime; datetimeoffset)";
    private const string TryCast = "TRY_CAST and TRY_CONVERT: overflow, argument errors, conversions not permitted (TRY_CAST)";
    private const string Guid = "Which GUID spellings convert to uniqueidentifier, and truncation to 36 characters (uniqueidentifier)";
    private const string ShortText = "CAST to a too-short text type; assignment to a short column (CAST and CONVERT)";
    private const string Concat = "Whether CONCAT of two nvarchar(4000) values is cut at 4000 characters (CONCAT)";
    private const string Trim = "TRIM, LTRIM, RTRIM remove only char(32) (TRIM, LTRIM, RTRIM)";
    private const string Len = "LEN keeps trailing white space other than spaces; LEN of a max type is bigint (LEN)";
    private const string FloatCompare = "float compared at full binary precision (float and real; comparison operators)";
    private const string Settings = "The settings the harness runs under (SET statements)";

    /// <summary>Every observation, in list order.</summary>
    public static readonly IReadOnlyList<ReferenceObservation> All =
    [
        E("R01a", Literals, "748.58", "748.58 : decimal(5,2)"),
        E("R01b", Literals, "0.5", "0.5 : decimal(1,1)"),
        E("R01c", Literals, "00012.3400", "12.3400 : decimal(6,4)"),
        E("R02a", Literals, "2147483647", "2147483647 : int"),
        E("R02b", Literals, "2147483648", "2147483648 : decimal(10,0)"),
        E("R02c", Literals, "-2147483648", "-2147483648 : int"),
        E("R02d", Literals, "99999999999999999999", "99999999999999999999 : decimal(20,0)"),
        E("R03a", Literals, "'abc'", "'abc' : varchar(3)"),
        E("R03b", Literals, "N'abc'", "'abc' : nvarchar(3)"),
        E("R03c", Literals, "''", "'' : varchar(1)"),
        E("R04a", Nulls, "NULL", "NULL : int"),
        E("R04b", Nulls, "ISNULL(NULL, NULL)", "NULL : int"),
        E("R04c", Nulls, "IIF(1 = 1, NULL, NULL)", "error TypeClash (8133)"),
        E("R04d", Nulls, "COALESCE(NULL, NULL)", "error TypeClash (4127)"),
        B("R04e", Nulls, "SELECT NULL AS n, ISNULL(NULL, NULL) AS i INTO #t; SELECT name, TYPE_NAME(system_type_id) AS type FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..#t') ORDER BY name; DROP TABLE #t;", "name='i', type='int' | name='n', type='int'"),
        E("R05a", Money, "CAST(1 AS money) / CAST(3 AS money)", "0.3333 : money"),
        E("R05b", Money, "CAST(2 AS money) / CAST(3 AS money)", "0.6666 : money"),
        E("R05c", Money, "CAST(-2 AS money) / CAST(3 AS money)", "-0.6666 : money"),
        E("R05d", Money, "CAST(1 AS money) / 3", "0.3333 : money"),
        E("R05e", Money, "CAST(2 AS money) / 3", "0.6666 : money"),
        E("R06a", Money, "CAST(1 AS money) / 3 * 3", "0.9999 : money"),
        E("R06b", Money, "CAST(10 AS money) * CAST(0.33335 AS money)", "3.3340 : money"),
        E("R06c", Money, "CAST(1 AS money) * 1.1", "1.10000 : decimal(22,5)"),
        E("R06d", Money, "CAST(1 AS money) * 2", "2.0000 : money"),
        E("R07a", ToMoney, "CAST(1.23455 AS money)", "1.2346 : money"),
        E("R07b", ToMoney, "CAST(-1.23455 AS money)", "-1.2346 : money"),
        E("R07c", ToMoney, "CAST(CAST(1.23455 AS float) AS money)", "1.2346 : money"),
        E("R07d", ToMoney, "CAST(CAST(2.5 AS money) AS int)", "3 : int"),
        E("R07e", ToMoney, "CAST(CAST(-2.5 AS money) AS int)", "-3 : int"),
        E("R08a", ToInt, "CAST(2.7 AS int)", "2 : int"),
        E("R08b", ToInt, "CAST(-2.7 AS int)", "-2 : int"),
        E("R08c", ToInt, "CAST(CAST(2.7 AS float) AS int)", "2 : int"),
        E("R08d", ToInt, "CAST(2.5 AS decimal(2,0))", "3 : decimal(2,0)"),
        E("R08e", ToInt, "CAST(-2.5 AS decimal(2,0))", "-3 : decimal(2,0)"),
        B("R08f", ToInt, "DECLARE @t TABLE (i int, d decimal(10,1)); INSERT @t VALUES (2.7, 1.25), (-2.7, -1.25); SELECT i, d FROM @t ORDER BY i DESC;", "i=2, d=1.3 | i=-2, d=-1.3"),
        E("R09a", MoneyToDecimal, "CAST(CAST(3148.79 AS money) AS decimal)", "3149 : decimal(18,0)"),
        E("R09b", MoneyToDecimal, "CAST(CAST(1.2399 AS money) AS decimal(10,2))", "1.24 : decimal(10,2)"),
        E("R09c", MoneyToDecimal, "CAST(CAST(-1.2399 AS money) AS decimal(10,2))", "-1.24 : decimal(10,2)"),
        B("R09d", MoneyToDecimal, "DECLARE @t TABLE (d decimal(10,2)); INSERT @t VALUES (CAST(1.2399 AS money)); SELECT d FROM @t;", "d=1.24"),
        E("R10a", Casing, "UPPER(N'i' COLLATE Turkish_CI_AS)", "'{U+0130}' : nvarchar(1)"),
        E("R10b", Casing, "UPPER(N'i' COLLATE Latin1_General_CI_AS)", "'I' : nvarchar(1)"),
        E("R10c", Casing, "UPPER(N'\u00DF' COLLATE Latin1_General_CI_AS)", "'{U+00DF}' : nvarchar(1)"),
        E("R10d", Casing, "UPPER(N'\u01C6' COLLATE Latin1_General_CI_AS)", "'{U+01C4}' : nvarchar(1)"),
        E("R10e", Casing, "LOWER(N'\u0130' COLLATE Latin1_General_CI_AS)", "'i' : nvarchar(1)"),
        E("R10f", Casing, "UPPER(N'i' COLLATE SQL_Latin1_General_CP1_CI_AS)", "'I' : nvarchar(1)"),
        E("R10g", Casing, "UNICODE(UPPER(N'\u00B5' COLLATE Latin1_General_100_CI_AS))", "181 : int"),
        E("R10h", Casing, "UPPER(N'\u00B5')", "'{U+00B5}' : nvarchar(1)"),
        E("R10i", Casing, "LOWER(N'\u0130')", "'i' : nvarchar(1)"),
        E("R11a", RoundOverflow, "ROUND(9.995, 2)", "error Overflow (8115)"),
        E("R11b", RoundOverflow, "ROUND(CAST(99.99 AS decimal(4,2)), 1)", "error Overflow (8115)"),
        E("R11c", RoundOverflow, "ROUND(CAST(9.5 AS decimal(2,1)), 0)", "error Overflow (8115)"),
        E("R12a", DateFormat, "CAST('2026-11-05' AS datetime)", "2026-11-05T00:00:00.0000000 : datetime"),
        B("R12b", DateFormat, "SET LANGUAGE us_english; SELECT CAST('2026-11-05' AS datetime) AS en; SET DATEFORMAT dmy; SELECT CAST('2026-11-05' AS datetime) AS dmy_dt, CAST('2026-11-05' AS date) AS dmy_date, CAST('20261105' AS datetime) AS dmy_compact; SET LANGUAGE French; SELECT CAST('2026-11-05' AS datetime) AS fr;", "en=2026-11-05T00:00:00.0000000 || dmy_dt=2026-05-11T00:00:00.0000000, dmy_date=2026-11-05T00:00:00.0000000, dmy_compact=2026-11-05T00:00:00.0000000 || fr=2026-05-11T00:00:00.0000000"),
        E("R13a", Offset, "CAST('2026-11-24T10:00:00Z' AS datetime)", "2026-11-24T10:00:00.0000000 : datetime"),
        E("R13b", Offset, "CAST('2026-11-24T10:00:00+05:00' AS datetime)", "error Conversion (241)"),
        E("R13c", Offset, "CAST('2026-11-24T10:00:00.123Z' AS datetime)", "2026-11-24T10:00:00.1230000 : datetime"),
        E("R14a", Offset, "CAST(CAST('2026-11-24T10:00:00+05:00' AS datetimeoffset) AS datetime)", "2026-11-24T10:00:00.0000000 : datetime"),
        E("R14b", Offset, "CAST(SWITCHOFFSET(CAST('2026-11-24T10:00:00+05:00' AS datetimeoffset), '+00:00') AS datetime)", "2026-11-24T05:00:00.0000000 : datetime"),
        E("R14c", Offset, "CAST(CAST('2026-11-24T10:00:00.1237+01:00' AS datetimeoffset(4)) AS datetime)", "2026-11-24T10:00:00.1230000 : datetime"),
        E("R15a", TryCast, "TRY_CAST(2147483648 AS int)", "NULL : int"),
        E("R15b", TryCast, "TRY_CAST(12345.6 AS decimal(4,1))", "NULL : decimal(4,1)"),
        E("R15c", TryCast, "TRY_CAST('abc' AS int)", "NULL : int"),
        E("R15d", TryCast, "TRY_CONVERT(int, 1e10)", "NULL : int"),
        E("R15e", TryCast, "TRY_CAST(1/0 AS int)", "error DivideByZero (8134)"),
        E("R15f", TryCast, "TRY_CAST(CAST(1 AS int) AS xml)", "error TypeClash (529)"),
        E("R16a", Guid, "CAST('{6F9619FF-8B86-D011-B42D-00C04FC964FF}' AS uniqueidentifier)", "6f9619ff-8b86-d011-b42d-00c04fc964ff : uniqueidentifier"),
        E("R16b", Guid, "CAST('6F9619FF8B86D011B42D00C04FC964FF' AS uniqueidentifier)", "error Conversion (8169)"),
        E("R16c", Guid, "CAST('(6F9619FF-8B86-D011-B42D-00C04FC964FF)' AS uniqueidentifier)", "error Conversion (8169)"),
        E("R16d", Guid, "CAST(' 6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)", "error Conversion (8169)"),
        E("R17a", Guid, "CAST('6F9619FF-8B86-D011-B42D-00C04FC964FFjunk' AS uniqueidentifier)", "6f9619ff-8b86-d011-b42d-00c04fc964ff : uniqueidentifier"),
        E("R17b", Guid, "CAST(N'6F9619FF-8B86-D011-B42D-00C04FC964FF-1234' AS uniqueidentifier)", "6f9619ff-8b86-d011-b42d-00c04fc964ff : uniqueidentifier"),
        E("R18a", ShortText, "CAST(N'abcde' AS nvarchar(3))", "'abc' : nvarchar(3)"),
        E("R18b", ShortText, "CAST(12345 AS nvarchar(3))", "error Overflow (8115)"),
        E("R18c", ShortText, "CAST(12345 AS varchar(3))", "'*' : varchar(3)"),
        B("R18d", ShortText, "DECLARE @t TABLE (c nvarchar(3)); INSERT @t VALUES (N'abc   '); SELECT c, DATALENGTH(c) AS bytes FROM @t;", "c='abc', bytes=6"),
        B("R18e", ShortText, "DECLARE @u TABLE (c nvarchar(3)); INSERT @u VALUES (N'abcd');", "error Truncation (2628)"),
        E("R19a", Concat, "LEN(CONCAT(REPLICATE(N'a', 4000), REPLICATE(N'b', 10)))", "4000 : int"),
        E("R19b", Concat, "SUBSTRING(CONCAT(REPLICATE(N'a', 4000), REPLICATE(N'b', 10)), 3998, 20)", "'aaa' : nvarchar(20)"),
        B("R19c", Concat, "DECLARE @a nvarchar(4000) = REPLICATE(N'a', 4000), @b nvarchar(4000) = REPLICATE(N'b', 10); SELECT LEN(CONCAT(@a, @b)) AS len_n; DECLARE @c varchar(8000) = REPLICATE('a', 8000); SELECT LEN(CONCAT(@c, 'x')) AS len_v;", "len_n=4000 || len_v=8000"),
        E("R20a", Trim, "DATALENGTH(TRIM(NCHAR(9) + N'a' + NCHAR(9)))", "6 : int"),
        E("R20b", Trim, "DATALENGTH(LTRIM(NCHAR(10) + N'a'))", "4 : int"),
        E("R20c", Trim, "DATALENGTH(RTRIM(N'a' + NCHAR(160)))", "4 : int"),
        E("R20d", Trim, "DATALENGTH(RTRIM(N'a' + NCHAR(13) + NCHAR(10)))", "6 : int"),
        E("R20e", Trim, "DATALENGTH(TRIM(NCHAR(12288) + N'a'))", "4 : int"),
        E("R20f", Trim, "TRIM(CHAR(9) + 'a' + CHAR(9))", "'{U+0009}a{U+0009}' : varchar(3)"),
        E("R20g", Trim, "LTRIM(CHAR(10) + 'a')", "'{U+000A}a' : varchar(2)"),
        E("R20h", Trim, "RTRIM('a' + CHAR(160))", "'a{U+00A0}' : varchar(2)"),
        E("R20i", Trim, "RTRIM('a' + CHAR(13) + CHAR(10))", "'a{U+000D}{U+000A}' : varchar(3)"),
        E("R20j", Trim, "TRIM(N'\u3000a')", "'{U+3000}a' : nvarchar(2)"),
        E("R21a", Len, "LEN(N'ab' + NCHAR(9))", "3 : int"),
        E("R21b", Len, "LEN(N'ab' + NCHAR(160))", "3 : int"),
        E("R21c", Len, "LEN(N'ab   ')", "2 : int"),
        E("R21d", Len, "LEN(N'  ab')", "4 : int"),
        E("R21e", Len, "LEN(N'   ')", "0 : int"),
        E("R21f", Len, "LEN('ab' + CHAR(9))", "3 : int"),
        E("R21g", Len, "LEN(CAST(N'ab' AS nvarchar(max)))", "2 : bigint"),
        E("R21h", Len, "LEN(N'ab')", "2 : int"),
        E("R22a", FloatCompare, "IIF(CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3, 'eq', 'ne')", "'ne' : varchar(2)"),
        E("R22b", FloatCompare, "IIF(CAST(0.1 AS float) + CAST(0.2 AS float) = CAST(0.3 AS float), 'eq', 'ne')", "'ne' : varchar(2)"),
        E("R22c", FloatCompare, "CASE WHEN CAST(1.0000000000000002 AS float) > 1.0 THEN 'gt' ELSE 'not' END", "'gt' : varchar(3)"),
        B("R24", Settings, "SELECT SESSIONPROPERTY('ANSI_WARNINGS') AS aw, SESSIONPROPERTY('ARITHABORT') AS ab, SESSIONPROPERTY('NUMERIC_ROUNDABORT') AS nra, SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL') AS cnyn, SESSIONPROPERTY('ANSI_NULLS') AS an, @@LANGUAGE AS lang, (SELECT date_format FROM sys.dm_exec_sessions WHERE session_id = @@SPID) AS datefmt, (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) AS compat;", "aw=1, ab=1, nra=0, cnyn=1, an=1, lang='us_english', datefmt='ymd', compat=160"),
        B("R25", Settings, "DECLARE @v nvarchar(3) = N'abcdef'; SELECT @v AS truncated_var;", "truncated_var='abc'"),
        E("W11", "ISNULL truncates its replacement to the first value's length (ISNULL)", "ISNULL(CAST(NULL AS nvarchar(3)), N'a longer default')", "'a l' : nvarchar(3)"),
    ];
}
