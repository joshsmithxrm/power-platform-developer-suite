using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Query;

/// <summary>
/// The admission matrix (offline: nothing connects). It asks the product, as a black box, whether each statement of a
/// declared grammar is admitted, and fails when an admitted combination is not proven by a passing case of the live
/// differential suite (<see cref="DmlLiveDifferentialTests"/>). A combination is claimed by a live case only when the
/// case's own SQL, parsed, contains it: the keys of both are computed by the same extractor from the parsed statement,
/// never written by hand, so a claim cannot drift from the SQL that ran.
/// </summary>
/// <remarks>
/// <para>The grammar, over the live suite's throwaway table (<see cref="DmlLiveTableShape"/>, every column type the
/// allowlist names plus the system columns that matter):</para>
/// <list type="bullet">
/// <item>WHERE: every comparison operator and predicate form (= &lt;&gt; != &lt; &gt; &lt;= &gt;= IN, NOT IN, LIKE,
/// NOT LIKE, BETWEEN, NOT BETWEEN, IS NULL, IS NOT NULL) on every column; every LIKE pattern form, negated or not; the
/// literal forms (GUID with dashes, braces, upper case, without dashes; date/time with Z, with an offset, without a time
/// zone; date only; N'...'; negative numbers); IN list sizes 1, 2..999, 1,000 and 1,001; AND, OR, parentheses and a
/// negated predicate inside OR.</item>
/// <item>Values: every arithmetic operator (+ - * / %) between every pair of number operand types (whole number,
/// decimal, currency, floating point; columns and literals) and unary minus on each; every allowed function on every
/// argument type (one-argument functions, CONCAT, SUBSTRING start/length boundaries, ROUND places 0, 1, 2, 10, 11);
/// COALESCE, ISNULL, NULLIF, IIF, CASE and simple CASE over every pair of value types; every comparison, IS NULL, AND,
/// OR and NOT inside IIF/CASE over every pair of types; CAST, CONVERT, TRY_CAST and TRY_CONVERT to every target
/// (including decimal(p, s) and nvarchar(n) at and beyond their bounds) from every source type.</item>
/// <item>Writes: every column written (UPDATE SET, INSERT VALUES, INSERT ... SELECT) from every value kind (each
/// literal kind, NULL, a GUID cast, a column of each type).</item>
/// <item>Forms: UPDATE and DELETE with dbo, FROM (aliased or not), qualified and upper-case names, no WHERE; INSERT
/// VALUES of 1, 2..10, 11..999 and 1,000 rows; INSERT ... SELECT with an aliased source, TOP n, TOP (n), TOP 0 and
/// 5,000, ORDER BY on every column type (one and several columns); scripts of several statements; each hint.</item>
/// </list>
/// <para>What it does not cover: expressions nested deeper than one operator or function inside another (the live suite
/// runs some, but the matrix does not enumerate them), combinations of three or more dimensions at once (an operator is
/// paired with operand types, a function with an argument type; a write is not combined with every expression shape),
/// column precisions, lengths and ranges other than the throwaway table's (decimal and currency with 2 places, text of
/// 100 and 200 characters), tables other than one custom table (activity tables, the customer lookup, multi-table
/// lookups, elastic or virtual tables), data volumes beyond the boundaries named above, statements across GO, and
/// other environments ([LABEL].table).</para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DmlAdmissionMatrixTests
{
    private const string T = DmlLiveTableShape.Table;
    private static readonly string Pk = DmlLiveTableShape.PrimaryId(T);
    private static readonly int[] Choices = DmlLiveTableShape.Choices;
    private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Guid2 = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly ITestOutputHelper _output;

    public DmlAdmissionMatrixTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EveryAdmittedCombination_IsProvenByALiveCase()
    {
        var live = await DmlLiveDifferentialTests.CatalogueAsync();
        var proven = live.Where(c => c.Proves).SelectMany(c => Keys(c.Sql)).ToHashSet(StringComparer.Ordinal);

        var generated = Generate().ToList();
        var problems = new List<string>();
        var admitted = new Dictionary<string, string>(StringComparer.Ordinal);
        var refused = 0;
        foreach (var (key, sql) in generated)
        {
            var keys = Keys(sql);
            if (!keys.Contains(key))
                problems.Add($"the grammar's statement for {key} does not contain it when parsed: {sql} (keys: {string.Join(", ", keys)})");
            var probe = await ProbeAsync(sql);
            if (probe.Admitted)
                admitted.TryAdd(key, sql);
            else
                refused++;
        }

        var unproven = admitted.Where(a => !proven.Contains(a.Key)).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        _output.WriteLine($"MATRIX generated {generated.Count} statements over {generated.Select(g => g.Key).Distinct().Count()} combinations; " +
                          $"admitted {generated.Count - refused} statements, {admitted.Count} combinations; refused {refused}; " +
                          $"live cases {live.Count(c => c.Proves)} differential + {live.Count(c => !c.Proves)} refused, claiming {proven.Count} combinations; " +
                          $"admitted without a live case: {unproven.Count}");
        foreach (var (key, sql) in unproven)
            problems.Add($"admitted but not proven by any live case: {key}   e.g. {(sql.Length > 200 ? sql[..200] + "..." : sql)}");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Each live case is decided at the gate as the suite expects: a differential case is admitted, a refusal case is
    /// refused there with its code, and a case that must fail while running (divide by zero, overflow, the row cap) is
    /// admitted at the gate, so its failure is the runtime one.
    /// </summary>
    [Fact]
    public async Task EveryLiveCase_IsDecidedAtTheGateAsTheSuiteExpects()
    {
        var problems = new List<string>();
        foreach (var c in await DmlLiveDifferentialTests.CatalogueAsync())
        {
            var probe = await ProbeAsync(c.Sql);
            var runtime = c.AtRuntime || c.ExpectedCode is ErrorCodes.Query.ExecutionFailed or ErrorCodes.Query.DmlRowCapExceeded;
            if (c.Proves || runtime)
            {
                if (!probe.Admitted)
                    problems.Add($"{c.Sql}\n    expected to be admitted, refused with {probe.Code}: {probe.Message}");
            }
            else if (probe.Admitted || probe.Code != c.ExpectedCode)
            {
                problems.Add($"{c.Sql}\n    expected refusal {c.ExpectedCode}, got {(probe.Admitted ? "admitted" : probe.Code + ": " + probe.Message)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ── the black-box probe ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the real service (guard, allowlist, hint checks and column validator against <see cref="DmlLiveTableShape"/>)
    /// whether the unconfirmed statement would be put to the user for confirmation (admitted) or refused. Nothing can run:
    /// the executor fails the test if it is called. The no-WHERE prevention is off, as the live suite runs those cases.
    /// </summary>
    internal static async Task<(bool Admitted, string? Code, string? Message)> ProbeAsync(string sql)
    {
        var service = new SqlQueryService(new NeverExecute(), new NoShakedown(), metadataProvider: new DmlLiveTableShape.Provider(T, Choices))
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development,
            EnvironmentSafetySettings = new QuerySafetySettings { PreventUpdateWithoutWhere = false, PreventDeleteWithoutWhere = false }
        };
        try
        {
            await service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions() });
            return (false, "ran without asking for confirmation", null);
        }
        catch (PpdsDmlConfirmationRequiredException)
        {
            return (true, null, null);
        }
        catch (PpdsException ex)
        {
            return (false, ex.ErrorCode, ex.Message);
        }
    }

    private sealed class NeverExecute : IQueryExecutor
    {
        public Task<QueryResult> ExecuteFetchXmlAsync(string fetchXml, int? pageNumber = null, string? pagingCookie = null, bool includeCount = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The admission probe must not execute anything.");

        public Task<QueryResult> ExecuteFetchXmlAllPagesAsync(string fetchXml, int maxRecords = 5000, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The admission probe must not execute anything.");
    }

    private sealed class NoShakedown : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
        }
    }

    // ── the grammar ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, AttributeMetadataDto> Columns =
        DmlLiveTableShape.Columns(T, Choices).ToDictionary(c => c.LogicalName, StringComparer.OrdinalIgnoreCase);

    /// <summary>A literal of the column's own kind (the validator checks the literal's form against the column).</summary>
    private static string Literal(AttributeMetadataDto c, int which = 0) => TypeName(c) switch
    {
        "Integer" or "BigInt" => which == 0 ? "1" : "2",
        "Picklist" => Choices[which].ToString(CultureInfo.InvariantCulture),
        "State" => which == 0 ? "0" : "1",
        "Status" => which == 0 ? "1" : "2",
        "Boolean" => which == 0 ? "1" : "0",
        "Decimal" or "Money" or "Double" => which == 0 ? "2.5" : "3.5",
        "String" or "Memo" or "Virtual(String)" => which == 0 ? "'a'" : "'b'",
        "DateTime" or "DateTime(TimeZoneIndependent)" => which == 0 ? "'2024-01-01T00:00:00Z'" : "'2024-06-01T00:00:00Z'",
        "DateOnly" or "DateTime(UserLocal, DateOnly)" => which == 0 ? "'2024-01-01'" : "'2024-06-01'",
        _ => which == 0 ? $"'{Guid1}'" : $"'{Guid2}'"
    };

    private static readonly string[] Comparisons = ["=", "<>", "!=", "<", ">", "<=", ">="];

    private static string Update(string where, string set = "ppds_note = 'x'") => $"UPDATE {T} SET {set} WHERE {where}";

    private const string All = "ppds_name IS NULL OR ppds_name IS NOT NULL";

    /// <summary>(the combination, a statement that contains it), for every combination of the grammar.</summary>
    internal static IEnumerable<(string Key, string Sql)> Generate()
    {
        // WHERE: every predicate form on every column
        foreach (var c in Columns.Values)
        {
            var type = TypeName(c);
            var a = Literal(c);
            var b = Literal(c, 1);
            foreach (var op in Comparisons)
                yield return ($"where:{op}:{type}", Update($"{c.LogicalName} {op} {a}"));
            yield return ($"where:IN:{type}", Update($"{c.LogicalName} IN ({a}, {b})"));
            yield return ($"where:NOT IN:{type}", Update($"{c.LogicalName} NOT IN ({a}, {b})"));
            yield return ($"where:LIKE:{type}", Update($"{c.LogicalName} LIKE 'a%'"));
            yield return ($"where:NOT LIKE:{type}", Update($"{c.LogicalName} NOT LIKE 'a%'"));
            yield return ($"where:BETWEEN:{type}", Update($"{c.LogicalName} BETWEEN {a} AND {b}"));
            yield return ($"where:NOT BETWEEN:{type}", Update($"{c.LogicalName} NOT BETWEEN {a} AND {b}"));
            yield return ($"where:IS NULL:{type}", Update($"{c.LogicalName} IS NULL"));
            yield return ($"where:IS NOT NULL:{type}", Update($"{c.LogicalName} IS NOT NULL"));
        }

        // LIKE pattern forms, on text and multiline text
        foreach (var column in new[] { "ppds_name", "ppds_memo" })
            foreach (var pattern in new[] { "abc", "ab%", "%ab", "%ab%", "a_c", "a%c", "%", "", "a[b]%", "trail %" })
                foreach (var negated in new[] { false, true })
                    yield return ($"like:{(negated ? "NOT LIKE" : "LIKE")}:{LikeForm(pattern)}", Update($"{column} {(negated ? "NOT LIKE" : "LIKE")} '{pattern}'"));

        // literal forms
        yield return ("literal:guid-dashes", Update($"{Pk} = '{Guid1}'"));
        yield return ("literal:guid-upper", Update($"{Pk} = '{Guid1.ToUpperInvariant()}'"));
        yield return ("literal:guid-braces", Update($"{Pk} = '{{{Guid1}}}'"));
        yield return ("literal:guid-nodashes", Update($"{Pk} = '{Guid1.Replace("-", "", StringComparison.Ordinal)}'"));
        yield return ("literal:guid-dashes", Update($"ppds_parent = '{Guid1}'"));
        yield return ("literal:guid-braces", Update($"ppds_parent = '{{{Guid1}}}'"));
        yield return ("literal:date-Z", Update("ppds_when < '2024-01-01T00:00:00Z'"));
        yield return ("literal:date-offset", Update("ppds_when < '2024-01-01T00:00:00+02:00'"));
        yield return ("literal:date-no-zone", Update("ppds_when < '2024-01-01T00:00:00'"));
        yield return ("literal:date-no-zone", Update("ppds_when < '2024-01-01'"));
        yield return ("literal:date-only", Update("ppds_day < '2024-01-01'"));
        yield return ("literal:date-only-with-time", Update("ppds_day < '2024-01-01T00:00:00Z'"));
        yield return ("literal:N-string", Update("ppds_name = N'a'"));
        yield return ("literal:negative", Update("ppds_num = -1"));
        yield return ("literal:negative", Update("ppds_amount = -1.5"));
        yield return ("literal:float", Update("ppds_float = 2.5E0"));
        yield return ("where:IN-size:1", Update("ppds_num IN (1)"));
        yield return ("where:IN-size:1000", Update($"ppds_num IN ({string.Join(", ", Enumerable.Range(1, 1000))})"));
        yield return ("where:IN-size:1001+", Update($"ppds_num IN ({string.Join(", ", Enumerable.Range(1, 1001))})"));

        // WHERE structure
        yield return ("where:AND", Update("ppds_num = 1 AND ppds_amount > 2"));
        yield return ("where:OR", Update("ppds_num = 1 OR ppds_amount > 2"));
        yield return ("where:parentheses", Update("(ppds_num = 1 OR ppds_num = 2) AND ppds_amount > 2"));
        yield return ("where:negated-inside-OR", Update("ppds_num = 1 OR ppds_note <> 'x'"));
        yield return ("where:NOT", Update("NOT (ppds_num = 1)"));
        yield return ("where:column-to-column", Update("ppds_name = ppds_note"));
        yield return ("where:function", Update("UPPER(ppds_name) = 'A'"));
        yield return ("where:subquery", Update($"ppds_num IN (SELECT ppds_num FROM {T} WHERE ppds_num = 1)"));

        // values: arithmetic between every pair of number operand types
        var operands = new (string Sql, string Target)[]
        {
            ("ppds_num", "ppds_num"), ("ppds_amount", "ppds_amount"), ("ppds_money", "ppds_money"), ("ppds_float", "ppds_float"),
            ("3", "ppds_num"), ("2.5", "ppds_amount"), ("ppds_name", "ppds_note")
        };
        foreach (var op in new[] { "+", "-", "*", "/", "%" })
        {
            foreach (var left in operands)
            {
                foreach (var right in operands)
                {
                    if (!left.Sql.StartsWith("ppds_", StringComparison.Ordinal) && !right.Sql.StartsWith("ppds_", StringComparison.Ordinal))
                        continue;
                    var expr = $"{left.Sql} {op} {right.Sql}";
                    var key = $"arith:{op}:{Category(Parse(left.Sql))}:{Category(Parse(right.Sql))}";
                    foreach (var target in new[] { "ppds_num", "ppds_amount", "ppds_money", "ppds_float" })
                        yield return (key, Update(All, $"{target} = {expr}"));
                }
            }
        }
        foreach (var operand in new[] { "ppds_num", "ppds_amount", "ppds_money", "ppds_float" })
            yield return ($"neg:{Category(Parse(operand))}", Update(All, $"{operand} = -{operand}"));

        // values: functions on every argument type
        var arguments = new[] { "ppds_name", "ppds_memo", "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "ppds_flag", "ppds_choice", "ppds_when", "ppds_day", "ppds_parent", "'a'", "3", "2.5", "NULL" };
        var allTargets = new[] { "ppds_note", "ppds_memo", "ppds_num", "ppds_amount", "ppds_money", "ppds_float" };
        foreach (var argument in arguments)
        {
            var category = Category(Parse(argument));
            foreach (var name in new[] { "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "LEN", "ABS" })
                foreach (var target in allTargets)
                    yield return ($"fn:{name}:{category}", Update(All, $"{target} = {name}({argument})"));
            foreach (var target in allTargets)
            {
                yield return ($"fn:SUBSTRING:{category}", Update(All, $"{target} = SUBSTRING({argument}, 1, 3)"));
                yield return ($"fn:ROUND:{category}", Update(All, $"{target} = ROUND({argument}, 1)"));
                yield return ($"fn:CONCAT:{category}", Update(All, $"{target} = CONCAT('x', {argument})"));
            }
        }
        foreach (var (start, length) in new[] { (1, 0), (1, 3), (2, 3), (3, 100), (0, 2), (1, -1) })
            yield return ($"substring:{start},{length}", Update(All, $"ppds_note = SUBSTRING(ppds_name, {start}, {length})"));
        foreach (var places in new[] { 0, 1, 2, 10, 11, -1 })
            foreach (var target in new[] { "ppds_amount", "ppds_money", "ppds_float", "ppds_num" })
                yield return ($"round-places:{places}", Update(All, $"{target} = ROUND({target}, {places})"));

        // values: COALESCE, ISNULL, NULLIF, IIF, CASE and simple CASE over every pair of value types
        var values = new[] { "ppds_name", "ppds_memo", "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "'a'", "3", "2.5", "NULL" };
        foreach (var x in values)
        {
            foreach (var y in values)
            {
                var forms = new (string Name, string Sql)[]
                {
                    ("COALESCE", $"COALESCE({x}, {y})"), ("ISNULL", $"ISNULL({x}, {y})"), ("NULLIF", $"NULLIF({x}, {y})"),
                    ("IIF", $"IIF(ppds_num > 2, {x}, {y})"), ("CASE", $"CASE WHEN ppds_num > 2 THEN {x} ELSE {y} END"),
                    ("SIMPLECASE", $"CASE ppds_num WHEN 1 THEN {x} ELSE {y} END")
                };
                foreach (var (name, sql) in forms)
                {
                    var key = ChoiceKey(name, Parse(sql));
                    foreach (var target in allTargets)
                        yield return (key, Update(All, $"{target} = {sql}"));
                }
            }
        }

        // values: conditions inside IIF/CASE
        var compared = new[] { "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "ppds_name", "3", "2.5", "'a'" };
        foreach (var x in compared)
        {
            foreach (var y in compared)
            {
                foreach (var op in Comparisons)
                    yield return ($"vcond:{op}", Update(All, $"ppds_num = IIF({x} {op} {y}, 1, 0)"));
                yield return ($"vcondtypes:{SortedPair(Category(Parse(x)), Category(Parse(y)))}", Update(All, $"ppds_num = IIF({x} > {y}, 1, 0)"));
            }
            yield return ("vcond:IS NULL", Update(All, $"ppds_num = IIF({x} IS NULL, 1, 0)"));
            yield return ("vcond:IS NOT NULL", Update(All, $"ppds_num = IIF({x} IS NOT NULL, 1, 0)"));
        }
        yield return ("vcond:AND", Update(All, "ppds_num = IIF(ppds_num > 1 AND ppds_amount > 2, 1, 0)"));
        yield return ("vcond:OR", Update(All, "ppds_num = IIF(ppds_num > 1 OR ppds_amount > 2, 1, 0)"));
        yield return ("vcond:parentheses", Update(All, "ppds_num = IIF((ppds_num > 1 OR ppds_amount > 2) AND ppds_num < 5, 1, 0)"));
        yield return ("vcond:NOT", Update(All, "ppds_num = IIF(NOT (ppds_num > 1), 1, 0)"));
        yield return ("vcond:BETWEEN", Update(All, "ppds_num = IIF(ppds_num BETWEEN 1 AND 2, 1, 0)"));
        yield return ("vcond:IN", Update(All, "ppds_num = IIF(ppds_num IN (1, 2), 1, 0)"));
        yield return ("vcond:LIKE", Update(All, "ppds_num = IIF(ppds_name LIKE 'a%', 1, 0)"));

        // values: conversions to every target from every source
        var conversionTargets = new[]
        {
            "int", "bigint", "decimal(1, 0)", "decimal(2, 1)", "decimal(10, 2)", "decimal(19, 4)", "decimal(28, 0)", "decimal(28, 10)", "decimal(28, 28)",
            "decimal(29, 2)", "decimal(38, 30)", "decimal", "decimal(10)", "numeric(10, 2)", "nvarchar(1)", "nvarchar(10)", "nvarchar(4000)",
            "nvarchar(4001)", "nvarchar", "nvarchar(max)", "varchar(10)", "uniqueidentifier", "datetime", "date", "float", "money", "bit"
        };
        var sources = new[] { "ppds_name", "ppds_memo", "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "ppds_flag", "ppds_when", "ppds_parent", "'5'", "5", "2.5", $"'{Guid1}'", "NULL" };
        foreach (var target in conversionTargets)
        {
            foreach (var source in sources)
            {
                var forms = new (string Kind, string Sql)[]
                {
                    ("CAST", $"CAST({source} AS {target})"), ("CONVERT", $"CONVERT({target}, {source})"),
                    ("TRY_CAST", $"TRY_CAST({source} AS {target})"), ("TRY_CONVERT", $"TRY_CONVERT({target}, {source})")
                };
                foreach (var (kind, sql) in forms)
                {
                    var key = $"conv:{kind}:{TargetClass(target)}:{Category(Parse(source))}";
                    foreach (var column in new[] { "ppds_note", "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "ppds_parent", "ppds_when" })
                    {
                        yield return (key, Update(All, $"{column} = {sql}"));
                        yield return ($"convtarget:{Normalize(target)}", Update(All, $"{column} = {sql}"));
                    }
                }
            }
        }

        // writes: every column from every value kind, by UPDATE, INSERT VALUES and INSERT ... SELECT
        var written = new[] { "'a'", "'2024-01-01T00:00:00Z'", "'2024-01-01'", $"'{Guid1}'", $"CAST('{Guid1}' AS uniqueidentifier)", "1", "0",
            Choices[0].ToString(CultureInfo.InvariantCulture), "-1", "2.5", "-2.5", "NULL", "N'a'" };
        var columnValues = new[] { "ppds_name", "ppds_memo", "ppds_num", "ppds_amount", "ppds_money", "ppds_float", "ppds_flag", "ppds_choice", "ppds_when", "ppds_day", "ppds_parent", "statecode", "statuscode", "ownerid", Pk };
        foreach (var c in Columns.Values)
        {
            var type = TypeName(c);
            foreach (var value in written.Concat(columnValues))
            {
                var kind = ValueKind(Parse(value));
                if (type is "String" or "Memo" && kind is "guid-literal" or "date-only-literal" or "date-time-literal")
                    kind = "text";
                var set = c.LogicalName == "statecode" ? $"statecode = {value}, statuscode = 2" : c.LogicalName == "statuscode" ? $"statecode = 1, statuscode = {value}" : $"{c.LogicalName} = {value}";
                yield return ($"write:{type}:{kind}", Update(All, set));
                yield return ($"writeverb:UPDATE:{type}", Update(All, set));
                if (!value.StartsWith("ppds_", StringComparison.Ordinal) && value != "statecode" && value != "statuscode" && value != "ownerid" && value != Pk)
                {
                    yield return ($"write:{type}:{kind}", $"INSERT INTO {T} ({c.LogicalName}) VALUES ({value})");
                    yield return ($"writeverb:INSERT VALUES:{type}", $"INSERT INTO {T} ({c.LogicalName}) VALUES ({value})");
                }
                else
                {
                    yield return ($"write:{type}:{kind}", $"INSERT INTO {T} ({c.LogicalName}) SELECT TOP 2 {value} FROM {T} ORDER BY ppds_num");
                    yield return ($"writeverb:INSERT SELECT:{type}", $"INSERT INTO {T} ({c.LogicalName}) SELECT TOP 2 {value} FROM {T} ORDER BY ppds_num");
                }
            }
        }

        // forms
        foreach (var (key, sql) in Forms())
            yield return (key, sql);
    }

    private static IEnumerable<(string Key, string Sql)> Forms()
    {
        yield return ("form:UPDATE", Update("ppds_num = 1"));
        yield return ("form:UPDATE dbo", $"UPDATE dbo.{T} SET ppds_note = 'x' WHERE ppds_num = 1");
        yield return ("form:UPDATE FROM alias", $"UPDATE a SET ppds_note = 'x' FROM {T} a WHERE a.ppds_num = 1");
        yield return ("form:UPDATE FROM alias", $"UPDATE a SET ppds_note = 'x' FROM dbo.{T} a WHERE a.ppds_num = 1");
        yield return ("form:UPDATE FROM table", $"UPDATE {T} SET ppds_note = 'x' FROM {T} WHERE {T}.ppds_num = 1");
        yield return ("form:UPDATE FROM table", $"UPDATE dbo.{T} SET ppds_note = 'x' FROM {T} WHERE ppds_num = 1");
        yield return ("form:UPDATE without WHERE", $"UPDATE {T} SET ppds_note = 'x'");
        yield return ("form:qualified column", $"UPDATE {T} SET ppds_note = 'x' WHERE {T}.ppds_num = 1");
        yield return ("form:upper-case names", $"UPDATE [{T.ToUpperInvariant()}] SET [PPDS_NOTE] = 'x' WHERE [PPDS_NUM] = 1");
        yield return ("form:several SET columns", Update("ppds_num = 1", "ppds_note = 'x', ppds_num = 2"));
        yield return ("form:UPDATE FROM join", $"UPDATE {T} SET ppds_note = 'x' FROM {T} JOIN {T} b ON b.ppds_num = {T}.ppds_num WHERE {T}.ppds_num = 1");
        yield return ("form:UPDATE TOP", $"UPDATE TOP (1) {T} SET ppds_note = 'x' WHERE ppds_num = 1");
        yield return ("form:DELETE", $"DELETE FROM {T} WHERE ppds_num = 1");
        yield return ("form:DELETE dbo", $"DELETE FROM dbo.{T} WHERE ppds_num = 1");
        yield return ("form:DELETE FROM alias", $"DELETE a FROM {T} a WHERE a.ppds_num = 1");
        yield return ("form:DELETE FROM table", $"DELETE {T} FROM {T} WHERE {T}.ppds_num = 1");
        yield return ("form:DELETE without WHERE", $"DELETE FROM {T}");
        yield return ("form:INSERT VALUES rows:1", $"INSERT INTO {T} (ppds_name) VALUES ('a')");
        yield return ("form:INSERT VALUES rows:2-10", $"INSERT INTO {T} (ppds_name) VALUES ('a'), ('b')");
        yield return ("form:INSERT VALUES rows:11-999", $"INSERT INTO {T} (ppds_name) VALUES {string.Join(", ", Enumerable.Range(1, 12).Select(i => $"('r{i}')"))}");
        yield return ("form:INSERT VALUES rows:1000", $"INSERT INTO {T} (ppds_name) VALUES {string.Join(", ", Enumerable.Range(1, 1000).Select(i => $"('r{i}')"))}");
        yield return ("form:INSERT VALUES rows:1001+", $"INSERT INTO {T} (ppds_name) VALUES {string.Join(", ", Enumerable.Range(1, 1001).Select(i => $"('r{i}')"))}");
        yield return ("form:INSERT dbo", $"INSERT INTO dbo.{T} (ppds_name) VALUES ('a')");
        yield return ("form:INSERT SELECT", $"INSERT INTO {T} (ppds_name) SELECT ppds_name FROM {T} WHERE ppds_num = 1");
        yield return ("form:INSERT SELECT FROM alias", $"INSERT INTO {T} (ppds_name) SELECT s.ppds_name FROM {T} s WHERE s.ppds_num = 1");
        yield return ("form:INSERT SELECT FROM dbo", $"INSERT INTO {T} (ppds_name) SELECT ppds_name FROM dbo.{T} WHERE ppds_num = 1");
        yield return ("form:INSERT SELECT aliased item", $"INSERT INTO {T} (ppds_name, ppds_note) SELECT ppds_name AS n, UPPER(ppds_name) AS u FROM {T} WHERE ppds_num = 1");
        yield return ("form:INSERT SELECT TOP n", $"INSERT INTO {T} (ppds_name) SELECT TOP 2 ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:INSERT SELECT TOP (n)", $"INSERT INTO {T} (ppds_name) SELECT TOP (2) ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:TOP value:0", $"INSERT INTO {T} (ppds_name) SELECT TOP (0) ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:TOP value:5000", $"INSERT INTO {T} (ppds_name) SELECT TOP 5000 ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:TOP value:5001+", $"INSERT INTO {T} (ppds_name) SELECT TOP 5001 ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:INSERT SELECT without TOP, with ORDER BY", $"INSERT INTO {T} (ppds_name) SELECT ppds_name FROM {T} ORDER BY ppds_num");
        yield return ("form:INSERT SELECT TOP without ORDER BY", $"INSERT INTO {T} (ppds_name) SELECT TOP 2 ppds_name FROM {T}");
        foreach (var c in Columns.Values)
        {
            yield return ($"form:ORDER BY:{TypeName(c)}:ASC", $"INSERT INTO {T} (ppds_name) SELECT TOP 2 ppds_name FROM {T} ORDER BY {c.LogicalName}");
            yield return ($"form:ORDER BY:{TypeName(c)}:DESC", $"INSERT INTO {T} (ppds_name) SELECT TOP 2 ppds_name FROM {T} ORDER BY {c.LogicalName} DESC");
        }
        yield return ("form:ORDER BY several columns", $"INSERT INTO {T} (ppds_name) SELECT TOP 2 ppds_name FROM {T} ORDER BY ppds_day, ppds_name");
        yield return ("form:script", $"UPDATE {T} SET ppds_note = 'x' WHERE ppds_num = 1; DELETE FROM {T} WHERE ppds_num = 2");
        yield return ("form:script", $"INSERT INTO {T} (ppds_name) VALUES ('a'); UPDATE {T} SET ppds_note = 'x' WHERE ppds_name = 'a'");
        foreach (var hint in new[] { "BATCH_SIZE 2", "BYPASS_PLUGINS", "BYPASS_FLOWS", "NOLOCK", "MAXDOP 1", "HASH_GROUP", "USE_TDS", "MAX_ROWS 5" })
        {
            var name = hint.Split(' ')[0];
            yield return ($"hint:{name}:UPDATE", $"-- ppds:{hint}\n" + Update("ppds_num = 1"));
            yield return ($"hint:{name}:DELETE", $"-- ppds:{hint}\nDELETE FROM {T} WHERE ppds_num = 1");
            yield return ($"hint:{name}:INSERT", $"-- ppds:{hint}\nINSERT INTO {T} (ppds_name) VALUES ('a'), ('b')");
        }
    }

    // ── the extractor: the combinations a statement contains ──────────────────────────────────────────

    private static ScalarExpression Parse(string expression)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        var parsed = parser.ParseExpression(new StringReader(expression), out var errors);
        if (errors.Count > 0)
            throw new InvalidOperationException($"'{expression}' does not parse: {errors[0].Message}");
        return parsed;
    }

    /// <summary>The combinations <paramref name="sql"/> contains, computed from its parsed statements.</summary>
    internal static HashSet<string> Keys(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (errors.Count > 0)
            return keys;

        foreach (Match hint in Regex.Matches(sql, @"--\s*ppds:([A-Za-z_]+)"))
        {
            var verb = Regex.Match(Regex.Replace(sql, "--[^\n]*", ""), @"\b(UPDATE|DELETE|INSERT)\b", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
            keys.Add($"hint:{hint.Groups[1].Value.ToUpperInvariant()}:{verb}");
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count > 1)
            keys.Add("form:script");
        foreach (var statement in statements)
            new Extractor(keys).Statement(statement);
        return keys;
    }

    private sealed class Extractor(HashSet<string> keys)
    {
        public void Statement(TSqlStatement statement)
        {
            switch (statement)
            {
                case UpdateStatement update:
                {
                    var spec = update.UpdateSpecification;
                    keys.Add("form:UPDATE");
                    if (spec.TopRowFilter != null)
                        keys.Add("form:UPDATE TOP");
                    Target("UPDATE", spec.Target, spec.FromClause);
                    var setCount = 0;
                    foreach (var set in spec.SetClauses.OfType<AssignmentSetClause>())
                    {
                        setCount++;
                        if (set.Column != null)
                            Write("UPDATE", set.Column, set.NewValue);
                        Value(set.NewValue);
                    }
                    if (setCount > 1 && !spec.SetClauses.OfType<AssignmentSetClause>().Any(s => ColumnName(s.Column) is "statecode" or "statuscode"))
                        keys.Add("form:several SET columns");
                    Where("UPDATE", spec.WhereClause);
                    break;
                }
                case DeleteStatement delete:
                {
                    var spec = delete.DeleteSpecification;
                    keys.Add("form:DELETE");
                    Target("DELETE", spec.Target, spec.FromClause);
                    Where("DELETE", spec.WhereClause);
                    break;
                }
                case InsertStatement insert:
                {
                    var spec = insert.InsertSpecification;
                    if (spec.Target is NamedTableReference { SchemaObject.SchemaIdentifier: not null })
                        keys.Add("form:INSERT dbo");
                    var columns = spec.Columns.ToList();
                    if (spec.InsertSource is ValuesInsertSource values)
                    {
                        var rows = values.RowValues.Count;
                        keys.Add("form:INSERT VALUES rows:" + (rows == 1 ? "1" : rows <= 10 ? "2-10" : rows < 1000 ? "11-999" : rows == 1000 ? "1000" : "1001+"));
                        foreach (var row in values.RowValues)
                        {
                            for (var i = 0; i < row.ColumnValues.Count && i < columns.Count; i++)
                            {
                                Write("INSERT VALUES", columns[i], row.ColumnValues[i]);
                                Value(row.ColumnValues[i]);
                            }
                        }
                    }
                    else if (spec.InsertSource is SelectInsertSource { Select: QuerySpecification select })
                    {
                        keys.Add("form:INSERT SELECT");
                        if (select.FromClause?.TableReferences.FirstOrDefault() is NamedTableReference source)
                        {
                            if (source.Alias != null)
                                keys.Add("form:INSERT SELECT FROM alias");
                            if (source.SchemaObject.SchemaIdentifier != null)
                                keys.Add("form:INSERT SELECT FROM dbo");
                        }
                        if (select.TopRowFilter is { } top)
                        {
                            keys.Add(top.Expression is ParenthesisExpression ? "form:INSERT SELECT TOP (n)" : "form:INSERT SELECT TOP n");
                            var value = (top.Expression is ParenthesisExpression p ? p.Expression : top.Expression) as IntegerLiteral;
                            var n = value != null && int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;
                            keys.Add("form:TOP value:" + (n == 0 ? "0" : n == 5000 ? "5000" : n > 5000 ? "5001+" : "1-4999"));
                            if (select.OrderByClause == null)
                                keys.Add("form:INSERT SELECT TOP without ORDER BY");
                        }
                        else if (select.OrderByClause != null)
                        {
                            keys.Add("form:INSERT SELECT without TOP, with ORDER BY");
                        }
                        if (select.OrderByClause != null)
                        {
                            if (select.OrderByClause.OrderByElements.Count > 1)
                                keys.Add("form:ORDER BY several columns");
                            foreach (var order in select.OrderByClause.OrderByElements)
                            {
                                if (order.Expression is ColumnReferenceExpression c && Column(c) is { } a)
                                    keys.Add($"form:ORDER BY:{TypeName(a)}:{(order.SortOrder == SortOrder.Descending ? "DESC" : "ASC")}");
                            }
                        }
                        for (var i = 0; i < select.SelectElements.Count; i++)
                        {
                            if (select.SelectElements[i] is not SelectScalarExpression item)
                                continue;
                            if (item.ColumnName != null)
                                keys.Add("form:INSERT SELECT aliased item");
                            if (i < columns.Count)
                                Write("INSERT SELECT", columns[i], item.Expression);
                            Value(item.Expression);
                        }
                        Where("INSERT", select.WhereClause);
                    }
                    break;
                }
            }
        }

        private void Target(string verb, TableReference target, FromClause? from)
        {
            if (target is NamedTableReference { SchemaObject.SchemaIdentifier: not null } && from == null)
                keys.Add($"form:{verb} dbo");
            if (target is NamedTableReference named && named.SchemaObject.BaseIdentifier.Value != named.SchemaObject.BaseIdentifier.Value.ToLowerInvariant())
                keys.Add("form:upper-case names");
            if (from == null)
                return;
            if (from.TableReferences.Count != 1 || from.TableReferences[0] is not NamedTableReference fromTable)
            {
                keys.Add($"form:{verb} FROM join");
                return;
            }
            keys.Add(fromTable.Alias != null ? $"form:{verb} FROM alias" : $"form:{verb} FROM table");
        }

        private void Write(string verb, ColumnReferenceExpression column, ScalarExpression value)
        {
            if (Column(column) is not { } attribute)
                return;
            var type = TypeName(attribute);
            var kind = ValueKind(value);
            if (type is "String" or "Memo" && kind is "guid-literal" or "date-only-literal" or "date-time-literal")
                kind = "text";
            keys.Add($"write:{type}:{kind}");
            keys.Add($"writeverb:{verb}:{type}");
        }

        // WHERE
        private void Where(string verb, WhereClause? where)
        {
            if (where?.SearchCondition == null)
            {
                keys.Add($"form:{verb} without WHERE");
                return;
            }
            Condition(where.SearchCondition, insideOr: false);
        }

        private void Condition(BooleanExpression condition, bool insideOr)
        {
            switch (condition)
            {
                case BooleanBinaryExpression binary:
                    var or = binary.BinaryExpressionType == BooleanBinaryExpressionType.Or;
                    keys.Add(or ? "where:OR" : "where:AND");
                    Condition(binary.FirstExpression, insideOr || or);
                    Condition(binary.SecondExpression, insideOr || or);
                    break;
                case BooleanParenthesisExpression paren:
                    keys.Add("where:parentheses");
                    Condition(paren.Expression, insideOr);
                    break;
                case BooleanNotExpression not:
                    keys.Add("where:NOT");
                    Condition(not.Expression, insideOr);
                    break;
                case BooleanComparisonExpression comparison:
                    Predicate(comparison.FirstExpression, Op(comparison.ComparisonType), insideOr && comparison.ComparisonType is not BooleanComparisonType.Equals and not BooleanComparisonType.LessThan
                        and not BooleanComparisonType.GreaterThan and not BooleanComparisonType.LessThanOrEqualTo and not BooleanComparisonType.GreaterThanOrEqualTo,
                        comparison.SecondExpression);
                    break;
                case InPredicate @in:
                    if (@in.Subquery != null)
                    {
                        keys.Add("where:subquery");
                        break;
                    }
                    Predicate(@in.Expression, @in.NotDefined ? "NOT IN" : "IN", insideOr && @in.NotDefined, [.. @in.Values]);
                    var count = @in.Values.Count;
                    keys.Add("where:IN-size:" + (count == 1 ? "1" : count < 1000 ? "2-999" : count == 1000 ? "1000" : "1001+"));
                    break;
                case LikePredicate like:
                    Predicate(like.FirstExpression, like.NotDefined ? "NOT LIKE" : "LIKE", insideOr && like.NotDefined, like.SecondExpression);
                    if (like.SecondExpression is StringLiteral pattern)
                        keys.Add($"like:{(like.NotDefined ? "NOT LIKE" : "LIKE")}:{LikeForm(pattern.Value)}");
                    break;
                case BooleanTernaryExpression between:
                    var not2 = between.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween;
                    Predicate(between.FirstExpression, not2 ? "NOT BETWEEN" : "BETWEEN", insideOr && not2, between.SecondExpression, between.ThirdExpression);
                    break;
                case BooleanIsNullExpression isNull:
                    Predicate(isNull.Expression, isNull.IsNot ? "IS NOT NULL" : "IS NULL", false);
                    break;
            }
        }

        private void Predicate(ScalarExpression subject, string op, bool negatedInsideOr, params ScalarExpression[] literals)
        {
            if (negatedInsideOr)
                keys.Add("where:negated-inside-OR");
            if (subject is not ColumnReferenceExpression column)
            {
                keys.Add(subject is FunctionCall ? "where:function" : "where:expression on the left");
                return;
            }
            if (column.MultiPartIdentifier.Identifiers.Count > 1)
                keys.Add("form:qualified column");
            if (column.MultiPartIdentifier.Identifiers.Last().Value != column.MultiPartIdentifier.Identifiers.Last().Value.ToLowerInvariant())
                keys.Add("form:upper-case names");
            if (Column(column) is not { } attribute)
                return;
            var type = TypeName(attribute);
            keys.Add($"where:{op}:{type}");
            foreach (var literal in literals)
            {
                switch (literal)
                {
                    case ColumnReferenceExpression:
                        keys.Add("where:column-to-column");
                        break;
                    case StringLiteral s:
                        if (s.IsNational)
                            keys.Add("literal:N-string");
                        LiteralForm(type, s.Value);
                        break;
                    case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative }:
                        keys.Add("literal:negative");
                        break;
                    case RealLiteral:
                        keys.Add("literal:float");
                        break;
                }
            }
        }

        private void LiteralForm(string type, string value)
        {
            if (type is "PrimaryId" or "Uniqueidentifier" or "Lookup" or "Owner")
            {
                if (Guid.TryParseExact(value, "D", out _))
                    keys.Add("literal:guid-dashes");
                else if (Guid.TryParseExact(value, "B", out _))
                    keys.Add("literal:guid-braces");
                else if (Guid.TryParse(value, out _))
                    keys.Add("literal:guid-nodashes");
                if (value.Any(char.IsUpper))
                    keys.Add("literal:guid-upper");
            }
            else if (type == "DateTime")
            {
                keys.Add(Regex.IsMatch(value, @"Z$") ? "literal:date-Z" : Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$") ? "literal:date-offset" : "literal:date-no-zone");
            }
            else if (type == "DateOnly")
            {
                keys.Add(Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$") ? "literal:date-only" : "literal:date-only-with-time");
            }
        }

        // values
        private void Value(ScalarExpression expression)
        {
            switch (expression)
            {
                case ColumnReferenceExpression column when Column(column) is { } a:
                    keys.Add($"vcol:{TypeName(a)}");
                    break;
                case ParenthesisExpression paren:
                    Value(paren.Expression);
                    break;
                case UnaryExpression unary:
                    if (unary.Expression is not (IntegerLiteral or NumericLiteral or RealLiteral))
                        keys.Add($"neg:{Category(unary.Expression)}");
                    Value(unary.Expression);
                    break;
                case BinaryExpression binary:
                    keys.Add($"arith:{Op(binary.BinaryExpressionType)}:{Category(binary.FirstExpression)}:{Category(binary.SecondExpression)}");
                    Value(binary.FirstExpression);
                    Value(binary.SecondExpression);
                    break;
                case CastCall cast:
                    Conversion("CAST", cast.DataType, cast.Parameter);
                    break;
                case ConvertCall convert:
                    Conversion("CONVERT", convert.DataType, convert.Parameter);
                    break;
                case TryCastCall tryCast:
                    Conversion("TRY_CAST", tryCast.DataType, tryCast.Parameter);
                    break;
                case TryConvertCall tryConvert:
                    Conversion("TRY_CONVERT", tryConvert.DataType, tryConvert.Parameter);
                    break;
                case FunctionCall function:
                {
                    var name = function.FunctionName.Value.ToUpperInvariant();
                    var arguments = function.Parameters;
                    if (name is "ISNULL")
                        keys.Add(ChoiceKey("ISNULL", function));
                    else if (name == "CONCAT")
                        foreach (var argument in arguments)
                            keys.Add($"fn:CONCAT:{Category(argument)}");
                    else if (arguments.Count > 0)
                        keys.Add($"fn:{name}:{Category(arguments[0])}");
                    if (name == "ROUND" && arguments.Count == 2)
                        keys.Add($"round-places:{Number(arguments[1])}");
                    if (name == "SUBSTRING" && arguments.Count == 3)
                        keys.Add($"substring:{Number(arguments[1])},{Number(arguments[2])}");
                    foreach (var argument in arguments)
                        Value(argument);
                    break;
                }
                case CoalesceExpression coalesce:
                    keys.Add(ChoiceKey("COALESCE", coalesce));
                    foreach (var e in coalesce.Expressions)
                        Value(e);
                    break;
                case NullIfExpression nullIf:
                    keys.Add(ChoiceKey("NULLIF", nullIf));
                    Value(nullIf.FirstExpression);
                    Value(nullIf.SecondExpression);
                    break;
                case IIfCall iif:
                    keys.Add(ChoiceKey("IIF", iif));
                    ValueCondition(iif.Predicate);
                    Value(iif.ThenExpression);
                    Value(iif.ElseExpression);
                    break;
                case SearchedCaseExpression searched:
                    keys.Add(ChoiceKey("CASE", searched));
                    foreach (var when in searched.WhenClauses)
                    {
                        ValueCondition(when.WhenExpression);
                        Value(when.ThenExpression);
                    }
                    if (searched.ElseExpression != null)
                        Value(searched.ElseExpression);
                    break;
                case SimpleCaseExpression simple:
                    keys.Add(ChoiceKey("SIMPLECASE", simple));
                    keys.Add("vcond:=");
                    foreach (var when in simple.WhenClauses)
                    {
                        keys.Add($"vcondtypes:{SortedPair(Category(simple.InputExpression), Category(when.WhenExpression))}");
                        Value(when.ThenExpression);
                    }
                    Value(simple.InputExpression);
                    if (simple.ElseExpression != null)
                        Value(simple.ElseExpression);
                    break;
            }
        }

        private void ValueCondition(BooleanExpression condition)
        {
            switch (condition)
            {
                case BooleanBinaryExpression binary:
                    keys.Add(binary.BinaryExpressionType == BooleanBinaryExpressionType.Or ? "vcond:OR" : "vcond:AND");
                    ValueCondition(binary.FirstExpression);
                    ValueCondition(binary.SecondExpression);
                    break;
                case BooleanParenthesisExpression paren:
                    keys.Add("vcond:parentheses");
                    ValueCondition(paren.Expression);
                    break;
                case BooleanNotExpression not:
                    keys.Add("vcond:NOT");
                    ValueCondition(not.Expression);
                    break;
                case BooleanComparisonExpression comparison:
                    keys.Add($"vcond:{Op(comparison.ComparisonType)}");
                    keys.Add($"vcondtypes:{SortedPair(Category(comparison.FirstExpression), Category(comparison.SecondExpression))}");
                    Value(comparison.FirstExpression);
                    Value(comparison.SecondExpression);
                    break;
                case BooleanIsNullExpression isNull:
                    keys.Add(isNull.IsNot ? "vcond:IS NOT NULL" : "vcond:IS NULL");
                    Value(isNull.Expression);
                    break;
                case BooleanTernaryExpression:
                    keys.Add("vcond:BETWEEN");
                    break;
                case InPredicate:
                    keys.Add("vcond:IN");
                    break;
                case LikePredicate:
                    keys.Add("vcond:LIKE");
                    break;
            }
        }

        private void Conversion(string kind, DataTypeReference type, ScalarExpression parameter)
        {
            var target = TypeText(type);
            keys.Add($"conv:{kind}:{TargetClass(target)}:{Category(parameter)}");
            keys.Add($"convtarget:{Normalize(target)}");
            Value(parameter);
        }
    }

    // ── classification shared by the grammar and the extractor ───────────────────────────────────────

    private static AttributeMetadataDto? Column(ColumnReferenceExpression column)
        => Columns.TryGetValue(column.MultiPartIdentifier.Identifiers.Last().Value, out var a) ? a : null;

    private static string? ColumnName(ColumnReferenceExpression? column) => column?.MultiPartIdentifier.Identifiers.Last().Value.ToLowerInvariant();

    /// <summary>The column type a rule of the allowlist is about (date kinds and the primary id told apart).</summary>
    internal static string TypeName(AttributeMetadataDto c)
    {
        if (c.AttributeOf != null)
            return $"Virtual({c.AttributeType})";
        return c.AttributeType switch
        {
            "Uniqueidentifier" => c.IsPrimaryId ? "PrimaryId" : "Uniqueidentifier",
            "DateTime" => (c.DateTimeBehavior, c.Format) switch
            {
                ("UserLocal", "DateAndTime") => "DateTime",
                ("DateOnly", _) => "DateOnly",
                ("UserLocal", _) => "DateTime(UserLocal, DateOnly)",
                _ => $"DateTime({c.DateTimeBehavior})"
            },
            _ => c.AttributeType
        };
    }

    /// <summary>The T-SQL type category of a value: int, decimal, money, float, text, null, guid, or the column type.</summary>
    private static string Category(ScalarExpression e)
    {
        static int Rank(string c) => c switch { "float" => 5, "decimal" => 4, "money" => 3, "bigint" => 2, "int" => 1, _ => 0 };
        static string Higher(string a, string b)
        {
            if (a == "null") return b;
            if (b == "null") return a;
            if (a == b) return a;
            return Rank(a) > 0 && Rank(b) > 0 ? (Rank(a) >= Rank(b) ? a : b) : "mixed";
        }

        switch (e)
        {
            case IntegerLiteral: return "int";
            case NumericLiteral: return "decimal";
            case RealLiteral: return "float";
            case StringLiteral: return "text";
            case NullLiteral: return "null";
            case ParenthesisExpression p: return Category(p.Expression);
            case UnaryExpression u: return Category(u.Expression);
            case BinaryExpression b: return Higher(Category(b.FirstExpression), Category(b.SecondExpression));
            case ColumnReferenceExpression column:
                return Column(column) is not { } a ? "unknown" : a.AttributeType switch
                {
                    "Integer" => "int",
                    "BigInt" => "bigint",
                    "Decimal" => "decimal",
                    "Money" => "money",
                    "Double" => "float",
                    "String" or "Memo" when a.AttributeOf == null => "text",
                    _ => TypeName(a)
                };
            case CastCall c: return TargetClass(TypeText(c.DataType));
            case ConvertCall c: return TargetClass(TypeText(c.DataType));
            case TryCastCall c: return TargetClass(TypeText(c.DataType));
            case TryConvertCall c: return TargetClass(TypeText(c.DataType));
            case FunctionCall f:
                var name = f.FunctionName.Value.ToUpperInvariant();
                return name switch
                {
                    "LEN" => "int",
                    "ABS" or "ROUND" when f.Parameters.Count > 0 => Category(f.Parameters[0]),
                    "ISNULL" when f.Parameters.Count == 2 => Category(f.Parameters[0]) == "null" ? Category(f.Parameters[1]) : Category(f.Parameters[0]),
                    "UPPER" or "LOWER" or "LTRIM" or "RTRIM" or "TRIM" or "SUBSTRING" or "CONCAT" => "text",
                    _ => "function"
                };
            case CoalesceExpression c: return c.Expressions.Select(Category).Aggregate("null", Higher);
            case NullIfExpression n: return Category(n.FirstExpression);
            case IIfCall i: return Higher(Category(i.ThenExpression), Category(i.ElseExpression));
            case SearchedCaseExpression s: return s.WhenClauses.Select(w => Category(w.ThenExpression)).Append(s.ElseExpression is null ? "null" : Category(s.ElseExpression)).Aggregate("null", Higher);
            case SimpleCaseExpression s: return s.WhenClauses.Select(w => Category(w.ThenExpression)).Append(s.ElseExpression is null ? "null" : Category(s.ElseExpression)).Aggregate("null", Higher);
            default: return "expression";
        }
    }

    /// <summary>COALESCE, IIF and CASE by the set of their value categories; ISNULL and NULLIF by the ordered pair.</summary>
    private static string ChoiceKey(string name, TSqlFragment node)
    {
        IEnumerable<ScalarExpression?> values = node switch
        {
            FunctionCall f => f.Parameters,
            CoalesceExpression c => c.Expressions,
            NullIfExpression n => [n.FirstExpression, n.SecondExpression],
            IIfCall i => [i.ThenExpression, i.ElseExpression],
            SearchedCaseExpression s => s.WhenClauses.Select(w => w.ThenExpression).Append(s.ElseExpression),
            SimpleCaseExpression s => s.WhenClauses.Select(w => w.ThenExpression).Append(s.ElseExpression),
            _ => []
        };
        var categories = values.Select(v => v is null ? "null" : Category(v));
        return name is "ISNULL" or "NULLIF"
            ? $"fn:{name}:{string.Join("+", categories)}"
            : $"fn:{name}:{string.Join("+", categories.Distinct().OrderBy(c => c, StringComparer.Ordinal))}";
    }

    private static string SortedPair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}+{b}" : $"{b}+{a}";

    /// <summary>The kind of a written value: a literal kind, NULL, a GUID cast, or the category of an expression.</summary>
    /// <remarks>
    /// A number literal and a number column of the same T-SQL type are one kind (int, decimal, money, float), as are text
    /// literals and text columns (text) except N'...': written to a column they behave the same. Literal text in the forms
    /// a date, a date and time or a GUID needs is its own kind.
    /// </remarks>
    private static string ValueKind(ScalarExpression value) => value switch
    {
        ParenthesisExpression p => ValueKind(p.Expression),
        NullLiteral => "NULL",
        IntegerLiteral or UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral } => "int",
        NumericLiteral or UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: NumericLiteral } => "decimal",
        RealLiteral => "float-literal",
        StringLiteral { IsNational: true } => "N-text-literal",
        StringLiteral s when Guid.TryParse(s.Value, out _) => "guid-literal",
        StringLiteral s when Regex.IsMatch(s.Value, @"^\d{4}-\d{2}-\d{2}$") => "date-only-literal",
        StringLiteral s when Regex.IsMatch(s.Value, @"^\d{4}-\d{2}-\d{2}T") => "date-time-literal",
        StringLiteral => "text",
        CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier }, Parameter: StringLiteral } => "guid-cast",
        _ => Category(value) is "int" or "decimal" or "money" or "float" or "text" or "bigint" ? Category(value)
            : value is ColumnReferenceExpression c && Column(c) is { } a ? $"column:{TypeName(a)}" : $"expression:{Category(value)}"
    };

    private static string LikeForm(string pattern)
    {
        if (pattern.Contains('[') || pattern.Contains(']'))
            return "brackets";
        if (pattern.Length == 0)
            return "empty";
        if (pattern.All(c => c == '%'))
            return "any";
        var inner = pattern.Trim('%');
        var wild = inner.Contains('%') || inner.Contains('_');
        var lead = pattern.StartsWith('%');
        var trail = pattern.EndsWith('%');
        var shape = (lead, trail) switch
        {
            (false, false) => "exact",
            (false, true) => "prefix",
            (true, false) => "suffix",
            _ => "contains"
        };
        return (wild ? "wildcard-" : "") + shape + (inner.EndsWith(' ') ? "-trailing-space" : "");
    }

    private static string Op(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => "=",
        BooleanComparisonType.NotEqualToBrackets => "<>",
        BooleanComparisonType.NotEqualToExclamation => "!=",
        BooleanComparisonType.LessThan => "<",
        BooleanComparisonType.GreaterThan => ">",
        BooleanComparisonType.LessThanOrEqualTo => "<=",
        BooleanComparisonType.GreaterThanOrEqualTo => ">=",
        _ => type.ToString()
    };

    private static string Op(BinaryExpressionType type) => type switch
    {
        BinaryExpressionType.Add => "+",
        BinaryExpressionType.Subtract => "-",
        BinaryExpressionType.Multiply => "*",
        BinaryExpressionType.Divide => "/",
        BinaryExpressionType.Modulo => "%",
        _ => type.ToString()
    };

    private static string Number(ScalarExpression e) => e switch
    {
        IntegerLiteral i => i.Value,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral i } => "-" + i.Value,
        _ => "expression"
    };

    private static string TypeText(DataTypeReference type)
    {
        if (type is not SqlDataTypeReference sql)
            return type.Name.BaseIdentifier.Value.ToLowerInvariant();
        var name = sql.SqlDataTypeOption.ToString().ToLowerInvariant();
        return sql.Parameters.Count == 0 ? name : $"{name}({string.Join(", ", sql.Parameters.Select(p => p is Literal l ? l.Value.ToLowerInvariant() : "?"))})";
    }

    private static string Normalize(string target) => target.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string TargetClass(string target)
    {
        var name = Normalize(target);
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? name : name[..paren];
    }
}
