using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text.Json;
using PPDS.Cli.Commands.Api;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Commands.Connections;
using PPDS.Cli.Commands.ConnectionReferences;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.DeploymentSettings;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.EnvironmentVariables;
using PPDS.Cli.Commands.Flows;
using PPDS.Cli.Commands.ImportJobs;
using PPDS.Cli.Commands.Internal;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Schema;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Commands.CustomApis;
using PPDS.Cli.Commands.DataProviders;
using PPDS.Cli.Commands.DataSources;
using PPDS.Cli.Commands.ServiceEndpoints;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Roles;
using PPDS.Cli.Commands.Serve;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Commands.ModelDrivenApps;
using PPDS.Cli.Commands.Views;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Commands.Solutions;
using PPDS.Cli.Commands.Users;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Logs;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Infrastructure;

// Real command factories/parsers only. Never invoke a command action or Program.Main.
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class CliEnvironmentAliasTests(ITestOutputHelper output)
{
    private const string Target = "https://fixture.example.test";

    [Fact]
    [Trait("AC", "AC-ALIAS-01")]
    public void LegacyInventory_IsCaptured()
    {
        var nodes = Walk(Root()).ToArray();
        var roles = nodes.SelectMany(n => n.Command.Options.Where(o => o.Aliases.Contains("-e"))
            .Select(o => new { path = n.Path, option = o.Name })).OrderBy(r => r.path, StringComparer.Ordinal).ThenBy(r => r.option, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(roles);
        Assert.Contains(roles, r => r.option == "--environment");
        Assert.Contains(roles, r => r.option == "--entity");
        Assert.Contains(roles, r => r.option == "--entities");
        output.WriteLine("PPDS_ALIAS_LEGACY_INVENTORY=" + JsonSerializer.Serialize(new
        {
            commandPaths = nodes.Select(n => n.Path).Order(StringComparer.Ordinal).ToArray(), legacyE = roles
        }));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-07")]
    public void PureFactory_ExistsAndControlsInternalGroup()
    {
        // Factory presence is an expanded contract; it is not labeled an old parser bug.
        Assert.NotNull(Factory);
        Assert.Contains(Walk(Root(true)), n => n.Path == "internal");
        Assert.DoesNotContain(Walk(Root(false)), n => n.Path == "internal");
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-02")]
    public void EveryEnvironmentOption_ExposesConsistentEnvAlias()
    {
        var definitions = Walk(Root()).SelectMany(n => n.Command.Options.Where(o => o.Name == "--environment")
            .Select(o => (Path: n.Path, Option: o))).ToArray();
        Assert.NotEmpty(definitions);
        foreach (var definition in definitions)
            Assert.True(definition.Option.Aliases.Contains("-env"), "Missing -env at " + definition.Path);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-03")]
    public void CompleteTree_MigrationAliasesHaveUniqueBindings()
    {
        foreach (var node in Walk(Root()))
        foreach (var alias in new[] { "-e", "-env", "--environment", "--entity", "--entities" })
        {
            var bound = node.Command.Options.Where(o => o.Name == alias || o.Aliases.Contains(alias)).Distinct().ToArray();
            Assert.True(bound.Length <= 1, "Ambiguous " + alias + " at " + node.Path);
        }
    }

    [Theory]
    [InlineData("metadata-entity", "--entity", "account")]
    [InlineData("data-entity", "--entity", "account")]
    [InlineData("data-entities", "--entities", "account,contact")]
    [InlineData("views-environment", "--environment", "https://fixture.example.test")]
    [InlineData("flows-environment", "--environment", "https://fixture.example.test")]
    [InlineData("solutions-environment", "--environment", "https://fixture.example.test")]
    [Trait("AC", "AC-ALIAS-01")]
    public void RealLegacyShortAlias_KeepsItsBoundValue(string scenario, string option, string expected)
    {
        var parsed = Root().Parse(Arguments(scenario, "-e")); Assert.Empty(parsed.Errors);
        var symbol = FindOption(parsed, option);
        var bound = Assert.IsType<OptionResult>(parsed.GetResult(symbol));
        Assert.Same(symbol, bound.Option);
        Assert.Equal(option, bound.Option.Name);
        var actual = scenario == "data-entities" ? string.Join(',', parsed.GetValue(Assert.IsType<Option<string[]>>(symbol))!)
            : parsed.GetValue(Assert.IsType<Option<string?>>(symbol));
        Assert.Equal(expected, actual);
        output.WriteLine("PPDS_ALIAS_TOKEN_TRACE=" + JsonSerializer.Serialize(parsed.Tokens.Select(t => new { t.Value, type = t.Type.ToString(), symbol = BoundSymbol(t)?.Name })));
    }

    [Theory]
    [InlineData("metadata-entity", true)]
    [InlineData("data-entity", true)]
    [InlineData("data-entities", true)]
    [InlineData("views-environment", false)]
    [InlineData("flows-environment", false)]
    [InlineData("solutions-environment", false)]
    [Trait("AC", "AC-ALIAS-04")]
    [Trait("AC", "AC-ALIAS-05")]
    public void ActualShortAlias_WarnsOnlyForEntityRoles(string scenario, bool warning)
    {
        var parsed = Root().Parse(Arguments(scenario, "-e")); Assert.Empty(parsed.Errors);
        using var writer = new StringWriter(); Warn(parsed, writer);
        if (warning) AssertSingleWarning(writer.ToString()); else Assert.Empty(writer.ToString());
    }

    [Theory]
    [InlineData("views", "-env")]
    [InlineData("flows", "-env")]
    [InlineData("solutions", "-env")]
    [InlineData("metadata", "-env")]
    [InlineData("data", "-env")]
    [InlineData("views", "--environment")]
    [InlineData("flows", "--environment")]
    [InlineData("solutions", "--environment")]
    [InlineData("metadata", "--environment")]
    [InlineData("data", "--environment")]
    [Trait("AC", "AC-ALIAS-02")]
    public void RealEnvironmentSpellings_Agree(string group, string spelling)
    {
        var args = group is "metadata" or "data"
            ? Arguments(group + "-entity", "--entity").Concat(new[] { spelling, Target }).ToArray()
            : Arguments(group + "-environment", spelling);
        var parsed = Root().Parse(args); Assert.Empty(parsed.Errors);
        Assert.Equal(Target, parsed.GetValue(Assert.IsType<Option<string?>>(FindOption(parsed, "--environment"))));
        using var writer = new StringWriter(); Warn(parsed, writer); Assert.Empty(writer.ToString());
    }

    [Theory]
    [InlineData("long-entity")]
    [InlineData("long-entities")]
    [InlineData("literal-value")]
    [InlineData("terminator")]
    [InlineData("unknown-option")]
    [Trait("AC", "AC-ALIAS-05")]
    public void NonLegacyTokenShapes_DoNotWarn(string shape)
    {
        ParseResult parsed;
        if (shape == "terminator")
        {
            var root = new RootCommand { new Argument<string[]>("values") { Arity = ArgumentArity.ZeroOrMore } };
            parsed = root.Parse(["--", "-e"]); Assert.Empty(parsed.Errors);
        }
        else if (shape == "unknown-option")
        { parsed = new RootCommand().Parse(["-e"]); Assert.NotEmpty(parsed.Errors); }
        else
        {
            var scenario = shape == "long-entities" ? "data-entities" : "data-entity";
            var args = Arguments(scenario, scenario == "data-entities" ? "--entities" : "--entity");
            if (shape == "literal-value")
            {
                var index = Array.IndexOf(args, "--entity"); args = args.Take(index).Concat(new[] { "--entity=-e" }).Concat(args.Skip(index + 2)).ToArray();
            }
            parsed = Root().Parse(args); Assert.Empty(parsed.Errors);
        }
        using var writer = new StringWriter(); Warn(parsed, writer); Assert.Empty(writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-ALIAS-04")]
    public void RepeatedMixedAliases_StillWarnOnce(bool shortFirst)
    {
        var args = Arguments("metadata-entity", shortFirst ? "-e" : "--entity").Concat(shortFirst ? new[] { "--entity", "account" } : new[] { "-e", "account" }).ToArray();
        var parsed = Root().Parse(args);
        // Retain parser repeat-option behavior; even a parsed invalid invocation must not reach auth.
        Assert.Contains(parsed.Tokens, t => t.Type == TokenType.Option && BoundSymbol(t)?.Name == "--entity" && t.Value == "-e");
        using var writer = new StringWriter(); Warn(parsed, writer); AssertSingleWarning(writer.ToString());
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-04")]
    public void AttachedShortAlias_Warns()
    {
        var args = Arguments("metadata-entity", "-e"); var at = Array.IndexOf(args, "-e");
        args = args.Take(at).Concat(new[] { "-e=account" }).Concat(args.Skip(at + 2)).ToArray();
        var parsed = Root().Parse(args); Assert.Empty(parsed.Errors);
        Assert.Equal("account", parsed.GetValue(Assert.IsType<Option<string?>>(FindOption(parsed, "--entity"))));
        output.WriteLine("PPDS_ALIAS_ATTACHED_TOKENS=" + JsonSerializer.Serialize(parsed.Tokens.Select(t => new { t.Value, type = t.Type.ToString(), symbol = BoundSymbol(t)?.Name })));
        using var writer = new StringWriter(); Warn(parsed, writer); AssertSingleWarning(writer.ToString());
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-06")]
    public void ProductionWarningOverload_UsesOnlyStderr()
    {
        var parsed = Root().Parse(Arguments("metadata-entity", "-e")); Assert.Empty(parsed.Errors);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try { Console.SetOut(stdout); Console.SetError(stderr); Warn(parsed, null); }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.Empty(stdout.ToString()); AssertSingleWarning(stderr.ToString());
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-08")]
    public void HelpMigration_IsVisible()
    {
        foreach (var option in Walk(Root()).SelectMany(n => n.Command.Options).Distinct()
                     .Where(o => o.Aliases.Contains("-e") && o.Name is "--entity" or "--entities"))
        {
            Assert.Contains(option.Name, option.Description ?? "", StringComparison.Ordinal);
            Assert.Contains("deprecat", option.Description ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TraceCorrelationCollision_IsRecordedSeparatelyFromAliasPolicy()
    {
        var root = Root();
        var list = root.Subcommands.Single(command => command.Name == "plugin-traces").Subcommands.Single(command => command.Name == "list");
        var options = list.Options.Where(option => option.Name == "--correlation-id").ToArray();
        Assert.NotEmpty(options);
        var parsed = root.Parse(Arguments("metadata-entity", "-e")); Assert.Empty(parsed.Errors);
        var value = parsed.GetValue(Assert.IsType<Option<string?>>(FindOption(parsed, "--entity"))); Assert.Equal("account", value);
        var error = Record.Exception(() => parsed.GetResult("--entity"));
        // Observe the inherited defect without requiring it as a permanent behavior contract.
        output.WriteLine("PPDS_ALIAS_LOOKUP_DIAGNOSTIC=" + JsonSerializer.Serialize(new
        { parseErrors = parsed.Errors.Count, typedEntity = value, correlationOptions = options.Length,
            guidOptions = options.Count(option => option is Option<Guid?>), stringOptions = options.Count(option => option is Option<string?>),
            distinctInstances = options.Length < 2 || !ReferenceEquals(options[0], options[1]),
            namedLookupErrorType = error?.GetType().FullName, commandActionsOrMainInvoked = false }));
    }

    private static Option FindOption(ParseResult parsed, string name)
    {
        var leaf = true;
        for (CommandResult? current = parsed.CommandResult; current is not null; current = current.Parent as CommandResult)
        {
            var matches = current.Command.Options.Where(option => (leaf || option.Recursive)
                && (option.Name == name || option.Aliases.Contains(name))).Distinct().ToArray();
            if (matches.Length != 0) return Assert.Single(matches);
            leaf = false;
        }
        throw new Xunit.Sdk.XunitException("No matching option in the selected command chain: " + name);
    }

    // Test oracle only: the pinned 2.0.14 getter is internal. Production policy must use public parser APIs.
    private static Symbol? BoundSymbol(Token token)
    {
        var property = typeof(Token).GetProperty("Symbol", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.Equal(typeof(Token), property.DeclaringType);
        Assert.Equal(typeof(Symbol), property.PropertyType);
        var getter = property.GetGetMethod(nonPublic: true);
        Assert.NotNull(getter);
        Assert.True(getter.IsAssembly);
        Assert.False(getter.IsStatic);
        Assert.Equal(typeof(Symbol), getter.ReturnType);
        var value = property.GetValue(token);
        return value is null ? null : Assert.IsAssignableFrom<Symbol>(value);
    }

    private static MethodInfo? Factory => typeof(PPDS.Cli.Program).GetMethod("CreateRootCommand", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(bool)], null);
    private static RootCommand Root(bool includeInternal = true) => Factory is { } method
        ? Assert.IsType<RootCommand>(method.Invoke(null, [includeInternal])) : BaselineRoot(includeInternal);
    private static void Warn(ParseResult parsed, TextWriter? writer)
    {
        var type = typeof(PPDS.Cli.Program).Assembly.GetType("PPDS.Cli.Infrastructure.EnvironmentAliasPolicy");
        if (type == null) return; // Missing feature before implementation, never a fabricated warning.
        var parameters = writer == null ? new[] { typeof(ParseResult) } : new[] { typeof(ParseResult), typeof(TextWriter) };
        var method = type.GetMethod("WarnIfDeprecatedEntityAliasUsed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
        Assert.NotNull(method); method.Invoke(null, writer == null ? new object[] { parsed } : [parsed, writer]);
    }
    private static void AssertSingleWarning(string warning)
    {
        Assert.Single(warning.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("-e", warning); Assert.Contains("--entit", warning);
        Assert.Contains("deprecat", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture.example.test", warning); Assert.DoesNotContain("account", warning);
    }
    private static string[] Arguments(string scenario, string alias) => scenario switch
    {
        "metadata-entity" => ["metadata", "attribute", "delete", "--solution", "Fixture", alias, "account", "--column", "new_column", "--dry-run"],
        "data-entity" => ["data", "delete", alias, "account", "--id", "11111111-1111-1111-1111-111111111111"],
        "data-entities" => ["data", "schema", alias, "account", "contact", "--output", Path.Combine(Path.GetTempPath(), "alias-schema.xml")],
        "views-environment" => ["views", "list", "--entity", "account", alias, Target],
        "flows-environment" => ["flows", "list", alias, Target],
        "solutions-environment" => ["solutions", "list", alias, Target],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
    private sealed record Node(Command Command, string Path);
    private static IEnumerable<Node> Walk(Command command, string path = "")
    {
        yield return new Node(command, path);
        foreach (var child in command.Subcommands)
        foreach (var node in Walk(child, (path.Length == 0 ? "" : path + " ") + child.Name)) yield return node;
    }
    private static RootCommand BaselineRoot(bool includeInternal)
    {
        // Exact B3 Program registration statements; generated as source text, no product run.
        var root = new RootCommand("Offline alias parser fixture");
        root.Subcommands.Add(ApiCommandGroup.Create());
        root.Subcommands.Add(AuthCommandGroup.Create());
        root.Subcommands.Add(EnvCommandGroup.Create());
        root.Subcommands.Add(EnvCommandGroup.CreateOrgAlias());
        root.Subcommands.Add(DataCommandGroup.Create());
        root.Subcommands.Add(PluginsCommandGroup.Create());
        root.Subcommands.Add(PluginTracesCommandGroup.Create());
        root.Subcommands.Add(ServiceEndpointsCommandGroup.Create());
        root.Subcommands.Add(CustomApisCommandGroup.Create());
        root.Subcommands.Add(DataProvidersCommandGroup.Create());
        root.Subcommands.Add(DataSourcesCommandGroup.Create());
        root.Subcommands.Add(MetadataCommandGroup.Create());
        root.Subcommands.Add(SchemaCommandGroup.Create());
        root.Subcommands.Add(QueryCommandGroup.Create());
        root.Subcommands.Add(SolutionsCommandGroup.Create());
        root.Subcommands.Add(ImportJobsCommandGroup.Create());
        root.Subcommands.Add(EnvironmentVariablesCommandGroup.Create());
        root.Subcommands.Add(FlowsCommandGroup.Create());
        root.Subcommands.Add(ConnectionsCommandGroup.Create());
        root.Subcommands.Add(ConnectionReferencesCommandGroup.Create());
        root.Subcommands.Add(DeploymentSettingsCommandGroup.Create());
        root.Subcommands.Add(UsersCommandGroup.Create());
        root.Subcommands.Add(RolesCommandGroup.Create());
        root.Subcommands.Add(ModelDrivenAppCommandGroup.Create());
        root.Subcommands.Add(ViewsCommandGroup.Create());
        root.Subcommands.Add(FormsCommandGroup.Create());
        root.Subcommands.Add(WebResourcesCommandGroup.Create());
        root.Subcommands.Add(PublishCommandGroup.Create());
        root.Subcommands.Add(ServeCommand.Create());
        root.Subcommands.Add(DocsCommand.Create());
        root.Subcommands.Add(VersionCommand.Create());
        root.Subcommands.Add(LogsCommandGroup.Create());
        root.Subcommands.Add(InteractiveCommand.Create());
        if (includeInternal) root.Subcommands.Add(InternalCommandGroup.Create());
        HelpCustomization.ApplyRequiredOptionStyle(root);
        return root;
    }
}
