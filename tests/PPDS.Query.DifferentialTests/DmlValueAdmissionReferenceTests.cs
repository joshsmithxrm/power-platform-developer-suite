using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Query.DifferentialTests.Harness;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// The admission table (<see cref="DmlValueAdmissionTable"/>) against the implementation and the reference SQL Server
/// (#1698, review round 2). Every cell is decided by the implementation as the table says (the planner's own refusals,
/// then the admission function of the target's kind); and every admitted cell's value is the one SQL Server stores when
/// the same literal is assigned to a column of the target's reference type. So an admitted literal that SQL Server would
/// store differently, or refuse, fails the test.
/// </summary>
/// <remarks>
/// The reference types: <c>nvarchar(4000)</c>, <c>int</c>, <c>decimal(38, 10)</c>, <c>money</c>, <c>float</c>,
/// <c>datetimeoffset(0)</c> (compared in UTC), <c>date</c>, <c>bit</c>, <c>int</c> (choice) and <c>uniqueidentifier</c>.
/// A user-local date and time is compared as an instant: Dataverse's own conversion, which the spec makes the rule's
/// (SQL Server's <c>datetime</c> refuses or drops an offset), is from the instant to UTC.
/// </remarks>
public sealed class DmlValueAdmissionReferenceTests(ReferenceDatabase database) : IClassFixture<ReferenceDatabase>
{
    /// <summary>The implementation's decision for a cell: "U", "S", or "=" followed by the value as the table writes it.</summary>
    private static string Decide(string literal, DmlValueAdmissionTable.Target target)
    {
        var fragment = new QueryParser().Parse(DmlValueAdmissionTable.Statement(literal, target));
        var statement = ((TSqlScript)fragment).Batches.Single().Statements.Single();
        if (DmlStatementSupport.FindRefusal(statement) is { } refusal)
            return Code(refusal.Code);
        var value = ((AssignmentSetClause)((UpdateStatement)statement).UpdateSpecification.SetClauses.Single()).NewValue;
        if (DmlStatementSupport.ToDmlLiteral(value) is not { } dmlLiteral)
            return "U";
        var admission = DmlValueAdmission.AdmitLiteral(dmlLiteral, target.Metadata, DmlValueUse.Write);
        return admission.IsAdmitted ? "=" + Render(admission.Value) : Code(admission.Code!);
    }

    private static string Code(string code) => code switch
    {
        "Query.DmlUnsupportedConstruct" => "U",
        "Query.DmlValueStoredDifferently" => "S",
        _ => "?" + code
    };

    private static string Render(object? value) => value switch
    {
        null => "null",
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => Plain(d),
        Microsoft.Xrm.Sdk.Money m => Plain(m.Value),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt when dt.Kind == DateTimeKind.Utc => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        Microsoft.Xrm.Sdk.OptionSetValue o => o.Value.ToString(CultureInfo.InvariantCulture),
        Microsoft.Xrm.Sdk.EntityReference r => r.Id.ToString(),
        _ => "<" + value.GetType().Name + ">"
    };

    private static string Plain(decimal d)
    {
        var text = d.ToString("0.############################", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Two renderings of a value of the target's kind are the same value (numbers compared as numbers).</summary>
    private static bool Same(string expected, string actual, string kind) => kind switch
    {
        "decimal" or "currency" or "whole number" or "choice"
            => decimal.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture) == decimal.Parse(actual, NumberStyles.Float, CultureInfo.InvariantCulture),
        "floating point" => double.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture) == double.Parse(actual, NumberStyles.Float, CultureInfo.InvariantCulture),
        "lookup" => Guid.Parse(expected) == Guid.Parse(actual),
        _ => expected == actual
    };

    private static bool SameCell(string expected, string actual, string kind)
        => expected.Length > 1 && actual.Length > 1 && expected[0] == '=' && actual[0] == '='
            ? expected[1..] == "null" || actual[1..] == "null" ? expected == actual : Same(expected[1..], actual[1..], kind)
            : expected == actual;

    /// <summary>Runs without a server: the implementation decides every cell as the table says.</summary>
    [Fact]
    public void TheImplementation_DecidesEveryCellAsTheTableSays()
    {
        var wrong = DmlValueAdmissionTable.Cells()
            .Select(c => (c.Literal, c.Target.Kind, c.Cell, Actual: Decide(c.Literal, c.Target)))
            .Where(c => !SameCell(c.Cell, c.Actual, c.Kind))
            .Select(c => $"{c.Literal} -> {c.Kind}: table {c.Cell}, implementation {c.Actual}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>Every admitted cell is the value SQL Server stores for the same literal in a column of the reference type.</summary>
    [ReferenceServerFact]
    public void EveryAdmittedCell_IsTheValueSqlServerStores()
    {
        var admitted = DmlValueAdmissionTable.Cells().Where(c => Decide(c.Literal, c.Target).StartsWith('=')).ToList();
        Assert.True(admitted.Count >= 170, $"only {admitted.Count} admitted cells");

        var wrong = new List<string>();
        foreach (var (literal, target, cell) in admitted)
        {
            var read = target.ReferenceType.StartsWith("datetimeoffset", StringComparison.Ordinal)
                ? "CONVERT(nvarchar(30), SWITCHOFFSET(v, '+00:00'), 127)"
                : target.ReferenceType == "date" ? "CONVERT(nvarchar(10), v, 23)" : "v";
            var sql = $"DECLARE @t TABLE (v {target.ReferenceType}); INSERT INTO @t (v) VALUES ({literal}); SELECT {read} FROM @t;";
            try
            {
                using var command = new SqlCommand(sql, database.Connection);
                var stored = command.ExecuteScalar();
                var actual = "=" + StoredText(stored, target.ReferenceType);
                if (!SameCell(cell, actual, target.Kind))
                    wrong.Add($"{literal} -> {target.Kind}: table and branch {cell}, SQL Server stores {actual}");
            }
            catch (SqlException ex)
            {
                wrong.Add($"{literal} -> {target.Kind}: table and branch {cell}, SQL Server refuses it ({ex.Number}: {ReferenceServer.Redact(ex.Message)})");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {admitted.Count} admitted cells differ from SQL Server:{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}");
    }

    private static string StoredText(object? stored, string referenceType) => stored switch
    {
        null or DBNull => "null",
        string s when referenceType.StartsWith("datetimeoffset", StringComparison.Ordinal) => s.Replace("+00:00", "Z", StringComparison.Ordinal).TrimEnd('Z') + "Z",
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => Plain(d),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        Guid g => g.ToString(),
        _ => "<" + stored.GetType().Name + ">"
    };
}
