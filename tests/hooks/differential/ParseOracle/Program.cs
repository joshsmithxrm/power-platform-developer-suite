// Parse-only ground truth for tests/hooks/differential (see ../README.md).
//
// Reads one JSON request per line on stdin and writes one JSON answer per line on stdout:
//   {"op":"tree"}                 every command path of the CLI's real root command
//   {"op":"cli","argv":[...]}     what the CLI's real parser makes of an argument vector
//   {"op":"mcp","argv":[...]}     what the MCP server's real option parser makes of one
//   {"op":"sql","text":"..."}     which statement kinds the product's SQL parser sees
//
// Safety: this program only ever calls RootCommand.Parse, McpSessionOptions.Parse and the
// product's QueryParser. It never calls Invoke/InvokeAsync and never runs a command action, so
// nothing here can sign in, read a profile's secrets or reach an environment.
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Mcp.Infrastructure;
using PPDS.Query.Parsing;

internal static class Program
{
    private static RootCommand? _root;

    private static RootCommand Root => _root ??= BuildRoot();

    // Program.CreateRootCommand is internal; call the exact method Program.Main uses (with the
    // internal group included, so the tree is the superset an agent could reach with PPDS_INTERNAL=1).
    private static RootCommand BuildRoot()
    {
        var program = typeof(PPDS.Cli.Program);
        var method = program.GetMethod(
            "CreateRootCommand",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(bool) },
            modifiers: null)
            ?? throw new InvalidOperationException("PPDS.Cli.Program.CreateRootCommand(bool) not found");
        return (RootCommand)method.Invoke(null, new object[] { true })!;
    }

    public static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonNode answer;
            try
            {
                var req = JsonNode.Parse(line)!.AsObject();
                var op = (string?)req["op"];
                answer = op switch
                {
                    "tree" => Tree(),
                    "cli" => Cli(Argv(req)),
                    "mcp" => Mcp(Argv(req)),
                    "sql" => Sql((string?)req["text"] ?? ""),
                    _ => new JsonObject { ["error"] = $"unknown op {op}" },
                };
                if (req["id"] is JsonNode id)
                    answer["id"] = id.DeepClone();
            }
            catch (Exception ex)
            {
                answer = new JsonObject { ["oracle_error"] = ex.GetType().Name + ": " + ex.Message, ["at"] = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() };
            }
            stdout.WriteLine(answer.ToJsonString());
            stdout.Flush();
        }
        return 0;
    }

    private static string[] Argv(JsonObject req) =>
        req["argv"]!.AsArray().Select(n => (string)n!).ToArray();

    // ---- tree -------------------------------------------------------------------------------

    private static JsonNode Tree()
    {
        var list = new JsonArray();
        void Walk(Command cmd, List<string> path, List<Option> inherited)
        {
            var options = new JsonArray();
            foreach (var o in inherited.Concat(cmd.Options).Distinct())
                options.Add(OptionInfo(o));
            var arguments = new JsonArray();
            foreach (var a in cmd.Arguments)
                arguments.Add(new JsonObject
                {
                    ["name"] = a.Name,
                    ["min"] = a.Arity.MinimumNumberOfValues,
                    ["max"] = a.Arity.MaximumNumberOfValues,
                    ["type"] = a.ValueType.Name,
                    ["enum"] = EnumNames(a.ValueType),
                });
            list.Add(new JsonObject
            {
                ["path"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray()),
                ["aliases"] = new JsonArray(cmd.Aliases.Select(p => (JsonNode)p).ToArray()),
                ["hidden"] = cmd.Hidden,
                ["has_action"] = cmd.Action != null,
                ["children"] = new JsonArray(cmd.Subcommands.Select(c => (JsonNode)c.Name).ToArray()),
                ["options"] = options,
                ["arguments"] = arguments,
            });
            var next = inherited.Concat(cmd.Options.Where(o => o.Recursive)).ToList();
            foreach (var sub in cmd.Subcommands)
                Walk(sub, path.Append(sub.Name).ToList(), next);
        }
        Walk(Root, new List<string>(), new List<Option>());
        return new JsonObject { ["commands"] = list };
    }

    private static JsonObject OptionInfo(Option o) => new()
    {
        ["name"] = o.Name,
        ["aliases"] = new JsonArray(o.Aliases.Select(p => (JsonNode)p).ToArray()),
        ["min"] = o.Arity.MinimumNumberOfValues,
        ["max"] = o.Arity.MaximumNumberOfValues,
        ["type"] = o.ValueType.Name,
        ["recursive"] = o.Recursive,
        ["required"] = o.Required,
        ["has_action"] = o.Action != null,
        ["enum"] = EnumNames(o.ValueType),
    };

    private static JsonArray? EnumNames(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u.IsEnum ? new JsonArray(Enum.GetNames(u).Select(n => (JsonNode)n).ToArray()) : null;
    }

    // ---- cli --------------------------------------------------------------------------------

    private static JsonNode Cli(string[] argv)
    {
        // Program.Main: no arguments at all launches the TUI before any parse.
        if (argv.Length == 0)
            return new JsonObject { ["tui"] = true, ["runs"] = true, ["path"] = new JsonArray(), ["errors"] = new JsonArray() };

        ParseResult pr;
        try
        {
            pr = Root.Parse(argv);
        }
        catch (Exception ex)
        {
            // Program.Main parses inside its try block: an exception from Parse (a product validator
            // that throws) is caught and rendered as an error, and no command runs.
            return new JsonObject
            {
                ["tui"] = false, ["runs"] = false, ["path"] = new JsonArray(),
                ["errors"] = new JsonArray((JsonNode)("parse threw " + ex.GetType().Name + ": " + ex.Message)),
            };
        }
        var path = new List<string>();
        for (SymbolResult? r = pr.CommandResult; r != null; r = r.Parent)
            if (r is CommandResult cr && cr.Command is not RootCommand)
                path.Insert(0, cr.Command.Name);

        var options = new JsonObject();
        var arguments = new JsonObject();
        void Collect(CommandResult r)
        {
            foreach (var child in r.Children)
            {
                switch (child)
                {
                    case OptionResult o:
                        options[o.Option.Name] = new JsonObject
                        {
                            ["tokens"] = Tokens(o.Tokens),
                            ["implicit"] = o.Implicit,
                            ["value"] = Value(() => o.GetValueOrDefault<object?>()),
                            ["identifier"] = o.IdentifierToken?.Value,
                        };
                        break;
                    case ArgumentResult a:
                        arguments[a.Argument.Name] = new JsonObject
                        {
                            ["tokens"] = Tokens(a.Tokens),
                            ["value"] = Value(() => a.GetValueOrDefault<object?>()),
                        };
                        break;
                    case CommandResult c:
                        Collect(c);
                        break;
                }
            }
        }
        string? collectError = null;
        try { Collect(pr.RootCommandResult); }
        catch (Exception ex) { collectError = ex.GetType().Name + ": " + ex.Message; }

        var action = pr.Action;
        var own = pr.CommandResult.Command.Action;
        var answer = new JsonObject
        {
            ["tui"] = false,
            ["path"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray()),
            ["errors"] = new JsonArray(pr.Errors.Select(e => (JsonNode)e.Message).ToArray()),
            ["action"] = action?.GetType().Name,
            ["runs"] = action != null && own != null && ReferenceEquals(action, own),
            ["options"] = options,
            ["arguments"] = arguments,
            ["unmatched"] = new JsonArray(pr.UnmatchedTokens.Select(t => (JsonNode)t).ToArray()),
            ["collect_error"] = collectError,
        };
        if (path.SequenceEqual(new[] { "query", "sql" }) && arguments["sql"]?["value"] is JsonValue v
            && v.TryGetValue<string>(out var text))
            answer["sql"] = Sql(text);
        return answer;
    }

    private static JsonArray Tokens(IReadOnlyList<Token> tokens) =>
        new(tokens.Select(t => (JsonNode)t.Value).ToArray());

    private static JsonNode? Value(Func<object?> get)
    {
        object? v;
        try { v = get(); }
        catch (Exception ex) { return "<error:" + ex.GetType().Name + ">"; }
        return v switch
        {
            null => null,
            string s => s,
            bool b => b,
            System.Collections.IEnumerable e => new JsonArray(e.Cast<object?>().Select(x => (JsonNode?)(x?.ToString())).ToArray()),
            _ => v.ToString(),
        };
    }

    // ---- mcp --------------------------------------------------------------------------------

    private static JsonNode Mcp(string[] argv)
    {
        // src/PPDS.Mcp/Program.cs: --version anywhere prints the version and exits before hosting.
        if (McpSessionOptions.IsVersionRequested(argv))
            return new JsonObject { ["runs"] = false, ["version"] = true };
        var o = McpSessionOptions.Parse(argv);
        return new JsonObject
        {
            ["runs"] = true,
            ["read_only"] = o.ReadOnly,
            ["profile"] = o.Profile,
            ["environment"] = o.Environment,
            ["allowed_env"] = new JsonArray(o.AllowedEnvironments.Select(e => (JsonNode)e).ToArray()),
        };
    }

    // ---- sql --------------------------------------------------------------------------------

    private static JsonNode Sql(string text)
    {
        var parser = new QueryParser();
        var ok = parser.TryParse(text, out var fragment, out var errors);
        var kinds = new JsonArray();
        if (ok && fragment != null)
        {
            var visitor = new StatementCollector();
            fragment.Accept(visitor);
            foreach (var k in visitor.Kinds)
                kinds.Add(k);
        }
        return new JsonObject
        {
            ["parsed"] = ok,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode)e.Message).ToArray()),
            ["kinds"] = kinds,
        };
    }

    private sealed class StatementCollector : TSqlFragmentVisitor
    {
        public List<string> Kinds { get; } = new();

        public override void Visit(TSqlStatement node)
        {
            var name = node.GetType().Name;
            if (node is SelectStatement s && s.Into != null)
                name = "SelectStatement+Into";
            Kinds.Add(name);
            base.Visit(node);
        }
    }
}
