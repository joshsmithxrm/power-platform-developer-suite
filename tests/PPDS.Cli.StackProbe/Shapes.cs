namespace PPDS.Cli.StackProbe;

/// <summary>
/// Statement shapes for the #1952 stack tests. Each takes n, the number of levels, chain links or items.
/// </summary>
internal static class Shapes
{
    private static string R(string unit, int n) => string.Concat(Enumerable.Repeat(unit, n));
    private static string J(string unit, int n, string separator) => string.Join(separator, Enumerable.Repeat(unit, n));

    private const string Where = "SELECT name FROM account WHERE ";
    private const string Value = "SELECT name FROM account WHERE numberofemployees = ";

    public static Func<int, string> Get(string name) =>
        All.TryGetValue(name, out var make) ? make : throw new ArgumentException($"Unknown shape '{name}'.");

    public static readonly Dictionary<string, Func<int, string>> All = new()
    {
        // Calibration: the worst measured cost per counted token (opener-only text, as an editor holds it while
        // a statement is typed) and closed forms.
        ["cut-subquery"] = n => Value + R("(SELECT ", n) + "1",
        ["cut-paren"] = n => Value + R("(", n) + "1",
        ["cut-case"] = n => Value + R("CASE ", n) + "1",
        ["cut-casewhen"] = n => Value + R("CASE WHEN ", n) + "1",
        ["cut-function"] = n => Value + R("ABS(", n) + "1",
        ["cut-begin"] = n => R("BEGIN ", n) + "SELECT",
        ["cut-rowconstructor"] = n => "SELECT * FROM (VALUES " + R("((SELECT 1, ", n) + "1",
        ["closed-subquery"] = n => Value + R("(SELECT ", n) + "1" + R(")", n),
        ["closed-paren"] = n => Value + R("(", n) + "1" + R(")", n),
        ["attimezone"] = n => "SELECT name FROM account WHERE createdon = createdon" + R(" AT TIME ZONE 'UTC'", n),
        ["or"] = n => Where + J("name = 'v'", n, " OR "),
        ["nestedcomment"] = n => "SELECT 1 " + R("/*", n) + R("*/", n),

        // The reviewer's bypass table (round 3 of #1698): each killed the process on a 1 MB stack.
        ["bypass-or-mergejoin"] = n => Where + J("name = 'v'", n, " OR ") + " OPTION (MERGE JOIN)",
        ["bypass-leftnested"] = n => Where + R("(", n) + "name = 'v'" + R(" OR name = 'v')", n),
        ["bypass-isdistinct"] = n => Where + J("name IS DISTINCT FROM 'v'", n, " OR "),
        ["bypass-delete-isdistinct"] = n => "DELETE FROM account WHERE " + J("name IS DISTINCT FROM 'v'", n, " OR "),
        ["bypass-mult-hex"] = n => Value + J("0x1", n, " * "),
        ["bypass-mult-case"] = n => Value + J("CASE WHEN 1 = 1 THEN 1 END", n, " * "),
        ["bypass-mult-timestamp"] = n => "SELECT name FROM account WHERE createdon = " + J("CURRENT_TIMESTAMP", n, " * "),
        ["bypass-update-mult-hex"] = n => "UPDATE account SET numberofemployees = " + J("0x1", n, " * ") + " WHERE name = 'v'",
        ["bypass-insert-mult-hex"] = n => "INSERT INTO account (numberofemployees) VALUES (" + J("0x1", n, " * ") + ")",
        ["bypass-shift-left"] = n => Value + J("1", n, " << "),
        ["bypass-shift-right"] = n => Value + J("1", n, " >> "),
        ["bypass-attimezone"] = n => "SELECT name FROM account WHERE createdon = createdon" + R(" AT TIME ZONE 'UTC'", n),
        ["bypass-join"] = n => "SELECT a0.name FROM account a0" + string.Concat(Enumerable.Range(1, n).Select(i => $" JOIN account a{i} ON a{i}.accountid = a0.accountid")),
        ["bypass-crossjoin"] = n => "SELECT a0.name FROM account a0" + string.Concat(Enumerable.Range(1, n).Select(i => $" CROSS JOIN account a{i}")),
        ["bypass-endconversation"] = n => R("BEGIN END CONVERSATION @h; ", n) + "SELECT 1 " + R("END ", n),
        ["bypass-intersect"] = n => J("SELECT name FROM account", n, " INTERSECT "),
        ["bypass-except"] = n => J("SELECT name FROM account", n, " EXCEPT "),
        ["bypass-case-in-when"] = n => Value + R("CASE WHEN ", n) + "1" + R(" = 1 THEN 1 END", n),
        ["bypass-case-paren"] = n => Value + R("CASE WHEN (1 = 1) THEN (", n) + "1" + R(") END", n),
        ["bypass-begintry"] = n => R("BEGIN TRY ", n) + "SELECT 1 " + R("END TRY BEGIN CATCH SELECT 0 END CATCH ", n),
        ["issue-delete-or"] = n => "DELETE FROM account WHERE " + J("name = 'v'", n, " OR "),
        ["script-if"] = n => R("IF 1 = 1 ", n) + "SELECT 1",
        ["script-begin"] = n => R("BEGIN ", n) + "SELECT 1 " + R("END ", n),
        ["script-begin-delete"] = n => R("BEGIN ", n) + "DELETE FROM account WHERE name = 'v' " + R("END ", n),
        ["script-try-deep"] = n => "BEGIN TRY SELECT " + J("1", n, " + ") + " END TRY BEGIN CATCH SELECT 0 END CATCH",
        ["script-write-then-deep"] = n => "INSERT INTO account (name) VALUES ('a');\nSELECT " + J("1", n, " + ") + ";",
        ["derived"] = n => "SELECT * FROM " + R("(SELECT * FROM ", n) + "account" + R(") t", n),
        ["json-modify"] = n => "SELECT JSON_MODIFY('{}', '$" + R(".a", n) + "', 1)",
        ["eval-arith"] = n => "SELECT " + J("1", n, " + "),

        // The reviewer's false-refusal table: each ran before and must still run.
        ["ok-comments-in-string"] = n => "SELECT name FROM account WHERE name = '" + R("/* ", n) + "'",
        ["ok-comment-statements"] = n => R("SELECT name FROM account /* c */; ", n),
        ["ok-insert-comment-values"] = n => "INSERT INTO account (name) VALUES " + J("('a /* b')", n, ", "),
        ["ok-line-comments-update"] = n => R("-- /* note\n", n) + "UPDATE account SET description = 'x' WHERE name = 'v'",
        ["ok-sequential-if"] = n => R("IF 1 = 1 SELECT 1; ", n),
        ["ok-or-arithmetic"] = n => "SELECT name FROM account WHERE " + J("numberofemployees = 1 + 2 * 3", n, " OR "),
        ["ok-between"] = n => "SELECT name FROM account WHERE " + J("numberofemployees BETWEEN 1 AND 2", n, " AND "),
        ["ok-union-block"] = n => "BEGIN " + R("SELECT name FROM account UNION ALL SELECT name FROM account; ", n) + "END",
        ["ok-insert-long-rows"] = n => "INSERT INTO account (name) VALUES " + J("('" + new string('a', 9_000) + "')", n, ", "),
        ["ok-update-long-text"] = n => "UPDATE account SET description = '" + new string('a', n) + "' WHERE name = 'v'",
        ["ok-in-list"] = n => "SELECT name FROM account WHERE numberofemployees IN (" + J("1", n, ", ") + ")",
        ["ok-in-list-strings"] = n => "SELECT name FROM account WHERE name IN (" + J("'value'", n, ", ") + ")",
        ["ok-insert-short-rows"] = n => "INSERT INTO account (name, description) VALUES " + J("('a', 'b')", n, ", "),
        ["ok-or-chain"] = n => Where + J("name = 'v'", n, " OR "),
    };
}
