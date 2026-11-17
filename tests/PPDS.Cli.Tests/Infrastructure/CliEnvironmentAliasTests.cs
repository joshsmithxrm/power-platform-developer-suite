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

    // Legacy cross-command short-alias clashes scheduled for CLI 2.0 (#1536). Exact: alias plus the precise
    // set of long names, aggregated over the whole tree. A new clashing alias, or a new long name for an
    // allowlisted alias, fails. Not covered here: a new command re-using an already-listed (alias, long name)
    // pair; one command binding an alias twice is covered by EveryCommand_ShortAliasesAreNeverBoundTwice.
    private static readonly Dictionary<string, string[]> LegacyAliasClashes = new(StringComparer.Ordinal)
    {
        // -e: entity / entity list / environment; reassigned to environment only in CLI 2.0.
        ["-e"] = ["--entities", "--entity", "--environment"],
        // Other legacy short aliases found by the inventory; each is bound to different long options in
        // different commands (e.g. -f is --output-format on most commands but --file on data load and
        // deployment-settings sync, and --force on several unregister/download/extract/clear commands).
        ["-a"] = ["--assembly", "--include-attributes"],
        ["-c"] = ["--color", "--column", "--config", "--correlation-id", "--count"],
        ["-f"] = ["--file", "--force", "--output-format"],
        ["-i"] = ["--include", "--index", "--input", "--interval"],
        ["-l"] = ["--label", "--level", "--limit"],
        ["-m"] = ["--include-managed", "--mapping", "--message", "--mode"],
        ["-n"] = ["--lines", "--name", "--top"],
        ["-o"] = ["--output", "--output-format"],
        ["-p"] = ["--password", "--profile"],
        ["-s"] = ["--schema", "--set", "--show", "--solution"],
        ["-t"] = ["--tenant", "--timeout", "--top", "--type"],
        ["-u"] = ["--user", "--user-mapping"],
        ["-v"] = ["--verbose", "--view"],
    };

    [Fact]
    [Trait("AC", "AC-ALIAS-10")]
    public void CompleteTree_ShortAliasesDoNotClashAcrossCommandsBeyondLegacyAllowlist()
    {
        var bindings = Walk(Root()).SelectMany(n => n.Command.Options)
            .SelectMany(o => ShortBindings(o))
            .GroupBy(b => b.Alias, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Long).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var clashes = bindings.Where(b => b.Value.Length > 1).ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);
        output.WriteLine("PPDS_ALIAS_CLASH_INVENTORY=" + JsonSerializer.Serialize(clashes.OrderBy(c => c.Key, StringComparer.Ordinal)));

        var unexpected = clashes.Where(c => !LegacyAliasClashes.TryGetValue(c.Key, out var allowed) || !allowed.SequenceEqual(c.Value))
            .Select(c => c.Key + " => " + string.Join(", ", c.Value)).ToArray();
        Assert.True(unexpected.Length == 0, "Short alias bound to different long options across commands: " + string.Join("; ", unexpected));
        var stale = LegacyAliasClashes.Keys.Where(k => !clashes.ContainsKey(k)).ToArray();
        Assert.True(stale.Length == 0, "Allowlisted clash no longer exists; remove from allowlist: " + string.Join(", ", stale));
    }

    // Same-command double bindings: one short alias bound to two different options on one command.
    // The allowlist is empty (#1536): ShortAliasShadowPolicy strips the shadowed short alias from the shared
    // option on any command where a command-specific option owns it, so any remaining double binding fails.
    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void EveryCommand_ShortAliasesAreNeverBoundTwice()
    {
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var node in Walk(Root()))
        foreach (var group in node.Command.Options.SelectMany(o => ShortBindings(o).Select(b => (b.Alias, Option: o)))
                     .GroupBy(b => b.Alias, StringComparer.Ordinal))
        {
            var longs = group.Select(g => LongName(g.Option)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (longs.Length > 1) found[(node.Path.Length == 0 ? "<root>" : node.Path) + "|" + group.Key] = longs;
        }
        Assert.True(found.Count == 0, "Short alias bound to more than one option on a single command: " +
            string.Join("; ", found.Select(f => f.Key + " => " + string.Join(", ", f.Value))));
    }

    private static readonly string[] SharedLongNames = ["--output-format", "--verbose", "--quiet"];

    // The 18 same-command clashes that existed before #1536: command|alias|owner|shared option.
    private static readonly string[] ExpectedFormerClashes =
    [
        "custom-apis unregister|-f|--force|--output-format",
        "deployment-settings sync|-f|--file|--output-format",
        "plugins download assembly|-f|--force|--output-format",
        "plugins download package|-f|--force|--output-format",
        "plugins extract|-f|--force|--output-format",
        "plugins unregister|-f|--force|--output-format",
        "query history clear|-f|--force|--output-format",
        "service-endpoints unregister|-f|--force|--output-format",
        "views add-column|-v|--view|--verbose",
        "views clear-filter|-v|--view|--verbose",
        "views clear-sort|-v|--view|--verbose",
        "views get|-v|--view|--verbose",
        "views remove-column|-v|--view|--verbose",
        "views reorder-columns|-v|--view|--verbose",
        "views set-fetchxml|-v|--view|--verbose",
        "views set-filter|-v|--view|--verbose",
        "views set-sort|-v|--view|--verbose",
        "views update-column|-v|--view|--verbose",
    ];

    // Un-policied tree: the same registrations production runs, without ShortAliasShadowPolicy.
    private static RootCommand PrePolicyRoot() => Program.CreateRootCommand(includeInternal: true, shadowShortAliases: false);

    // Computed from the pre-policy tree only: a shared-named option whose short alias is also bound by an option
    // registered before it on the same command (the parser gives the alias to the earlier option).
    private static string[] FormerClashKeys(RootCommand prePolicy)
    {
        var keys = new List<string>();
        foreach (var node in Walk(prePolicy))
        {
            var options = node.Command.Options.ToList();
            for (var i = 0; i < options.Count; i++)
            {
                var shared = options[i];
                if (!SharedLongNames.Contains(shared.Name)) continue;
                foreach (var binding in ShortBindings(shared))
                foreach (var owner in options.Take(i).Where(o => o.Name != shared.Name && ShortBindings(o).Any(b => b.Alias == binding.Alias)))
                    keys.Add(node.Path + "|" + binding.Alias + "|" + LongName(owner) + "|" + shared.Name);
            }
        }
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void ShadowedAliasRemoval_CoversExactlyTheFormerSameCommandClashes()
    {
        var pre = PrePolicyRoot();
        var post = Root();
        var clashes = FormerClashKeys(pre);
        Assert.NotEmpty(clashes);
        Assert.Equal(ExpectedFormerClashes.Order(StringComparer.Ordinal), clashes);

        var preNodes = Walk(pre).ToList();
        var postNodes = Walk(post).ToList();
        Assert.Equal(preNodes.Select(n => n.Path), postNodes.Select(n => n.Path));
        var changed = new List<string>();
        for (var n = 0; n < preNodes.Count; n++)
        {
            var before = preNodes[n].Command.Options.ToList();
            var after = postNodes[n].Command.Options.ToList();
            Assert.Equal(before.Select(o => o.Name), after.Select(o => o.Name)); // same options, same order
            for (var i = 0; i < before.Count; i++)
            {
                Assert.Empty(after[i].Aliases.Except(before[i].Aliases));
                foreach (var alias in before[i].Aliases.Except(after[i].Aliases))
                {
                    var owner = before.Take(i).Single(o => ShortBindings(o).Any(b => b.Alias == alias));
                    changed.Add(preNodes[n].Path + "|" + alias + "|" + LongName(owner) + "|" + before[i].Name);
                }
            }
        }
        // No more, no fewer: aliases were removed on exactly the former clashes and nowhere else.
        Assert.Equal(clashes, changed.Order(StringComparer.Ordinal));

        // Where the shared --output-format keeps its short alias, it is -f (-o on data load).
        var outputFormats = postNodes.SelectMany(n => n.Command.Options.Where(o => o.Name == "--output-format").Select(o => (n.Path, Option: o))).ToList();
        Assert.NotEmpty(outputFormats);
        foreach (var (path, option) in outputFormats.Where(x => !clashes.Any(c => c.StartsWith(x.Path + "|", StringComparison.Ordinal))))
            Assert.Equal(path == "data load" ? new[] { "-o" } : new[] { "-f" }, option.Aliases.Where(a => a.Length == 2).ToArray());
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void ShadowedAliasRemoval_DoesNotChangeParsing_ComparedWithPrePolicyTree()
    {
        var pre = PrePolicyRoot();
        var post = Root();
        var clashes = FormerClashKeys(pre);
        Assert.NotEmpty(clashes);
        foreach (var key in clashes)
        {
            var parts = key.Split('|');
            var path = parts[0].Split(' ');
            var owner = Walk(pre).Single(n => n.Path == parts[0]).Command.Options.Single(o => LongName(o) == parts[2]);
            var ownerArgs = owner.ValueType == typeof(bool) ? new[] { parts[1] } : new[] { parts[1], "x" };
            var longForm = parts[3] == "--output-format" ? new[] { "--output-format", "json" } : new[] { "--verbose" };

            foreach (var args in new[] { ownerArgs, longForm, ownerArgs.Concat(longForm).ToArray() })
            {
                var tokens = path.Concat(args).ToArray();
                var before = pre.Parse(tokens);
                var after = post.Parse(tokens);
                Assert.Equal(Outcome(before), Outcome(after));
                Assert.Equal(before.Errors.Select(e => e.Message), after.Errors.Select(e => e.Message));
                Assert.Equal(GlobalOptions.GetValues(before).IsJsonMode, GlobalOptions.GetValues(after).IsJsonMode);
                Assert.Equal(GlobalOptions.GetValues(before).Verbose, GlobalOptions.GetValues(after).Verbose);
            }

            // The command-specific option still owns the alias after the policy.
            var owned = post.Parse(path.Concat(ownerArgs).ToArray());
            Assert.Contains(owner.Name + "=", Outcome(owned).Select(o => o.Split('=')[0] + "="));
            // The long form of the shared option still works.
            var longParsed = GlobalOptions.GetValues(post.Parse(path.Concat(longForm).ToArray()));
            Assert.True(parts[3] == "--output-format" ? longParsed.IsJsonMode : longParsed.Verbose, key);
        }
    }

    // Explicitly bound options of the selected command as "name=value".
    private static string[] Outcome(ParseResult parsed) =>
        parsed.CommandResult.Children.OfType<OptionResult>().Where(r => !r.Implicit)
            .Select(r => r.Option.Name + "=" + r.GetValueOrDefault<object?>()).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void TwinOnViews_KeepsMutualExclusionAndOtherSharedOptions()
    {
        var root = Root();
        string[] baseArgs = ["views", "get", "--entity", "account", "--view", "v1"];
        foreach (var conflict in new[] { "--quiet", "--debug" })
            Assert.Contains(root.Parse(baseArgs.Append("--verbose").Append(conflict).ToArray()).Errors,
                e => e.Message.Contains("mutually exclusive", StringComparison.Ordinal));
        Assert.DoesNotContain(root.Parse(baseArgs.Append("--verbose").ToArray()).Errors,
            e => e.Message.Contains("mutually exclusive", StringComparison.Ordinal));

        Assert.True(GlobalOptions.GetValues(root.Parse(baseArgs.Append("--quiet").ToArray())).Quiet);
        Assert.True(GlobalOptions.GetValues(root.Parse(baseArgs.Append("--debug").ToArray())).Debug);
        Assert.True(GlobalOptions.GetValues(root.Parse(baseArgs.Append("--verbose").ToArray())).Verbose);
        Assert.Equal("corr-1", GlobalOptions.GetValues(root.Parse(baseArgs.Concat(new[] { "--correlation-id", "corr-1" }).ToArray())).CorrelationId);
        var plain = GlobalOptions.GetValues(root.Parse(baseArgs));
        Assert.False(plain.Verbose || plain.Quiet || plain.Debug);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void TwinOnPluginsUnregister_KeepsOutputFormatValidationAndDefault()
    {
        var root = Root();
        string[] baseArgs = ["plugins", "unregister", "step", "00000000-0000-0000-0000-000000000001"];
        Assert.Contains(root.Parse(baseArgs.Concat(new[] { "--output-format", "xml" }).ToArray()).Errors,
            e => e.Message.Contains("Invalid value 'xml' for --output-format", StringComparison.Ordinal));
        Assert.Contains(root.Parse(baseArgs.Concat(new[] { "--output-format", "csv" }).ToArray()).Errors,
            e => e.Message.Contains("CSV output is not supported", StringComparison.Ordinal));
        Assert.Equal(OutputFormat.Text, GlobalOptions.GetValues(root.Parse(baseArgs)).OutputFormat);
        Assert.Equal(OutputFormat.Json, GlobalOptions.GetValues(root.Parse(baseArgs.Concat(new[] { "--output-format", "json" }).ToArray())).OutputFormat);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void ShortAliasShadowPolicy_AppliedTwice_ChangesNothing()
    {
        var root = Root();
        var before = Walk(root).SelectMany(n => n.Command.Options.Select(o => (Option: o, Names: o.Aliases.Prepend(o.Name).ToArray()))).ToList();
        ShortAliasShadowPolicy.Apply(root);
        var after = Walk(root).SelectMany(n => n.Command.Options.Select(o => (Option: o, Names: o.Aliases.Prepend(o.Name).ToArray()))).ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Same(before[i].Option, after[i].Option);
            Assert.Equal(before[i].Names, after[i].Names);
        }
    }

    [Theory]
    [InlineData("plugins unregister step", "-f")]
    [InlineData("views get", "-v")]
    [Trait("AC", "AC-ALIAS-11")]
    public void RenderedHelp_ListsEachShortAliasOnce(string path, string alias)
    {
        var root = Root();
        var writer = new StringWriter();
        root.Parse(path.Split(' ').Append("--help").ToArray()).Invoke(new InvocationConfiguration { Output = writer, Error = writer });
        var help = writer.ToString();
        output.WriteLine(help);
        var pattern = new System.Text.RegularExpressions.Regex(@"(^|[\s,])" + alias + @"(,|\s)", System.Text.RegularExpressions.RegexOptions.Multiline);
        var lines = help.Split('\n').Where(l => pattern.IsMatch(l.TrimStart())).ToArray();
        Assert.True(lines.Length == 1, "Expected " + alias + " once, found: " + string.Join(" || ", lines));
        Assert.Contains("--output-format", help);
        Assert.Contains("--verbose", help);
    }

    // Characterizes which option owns a formerly double-bound short alias: the command-specific option (unchanged by #1536).
    [Theory]
    [InlineData("deployment-settings sync|-f", "deployment-settings sync -f json", "--file")]
    [InlineData("views get|-v", "views get --entity account -v x", "--view")]
    [InlineData("plugins unregister|-f", "plugins unregister -f json", "--force")]
    [Trait("AC", "AC-ALIAS-11")]
    public void DoubleBoundShortAlias_ParserOutcome_IsCharacterized(string key, string commandLine, string winner)
    {
        var path = key.Split('|')[0]; var alias = key.Split('|')[1];
        var root = Root();
        var command = Walk(root).Single(n => n.Path == path).Command;
        var parsed = root.Parse(commandLine.Split(' '));
        var bound = command.Options.Where(o => ShortBindings(o).Any(b => b.Alias == alias))
            .Select(o => o.Name + "=" + (parsed.GetResult(o) is OptionResult r && !r.Implicit ? "BOUND" : "unbound")).Order(StringComparer.Ordinal);
        output.WriteLine("PPDS_ALIAS_DOUBLE_PARSE " + key + " :: " + string.Join(", ", bound) +
            " :: errors=[" + string.Join(" | ", parsed.Errors.Select(e => e.Message)) + "]");
        // The command-specific option (added first) takes the alias; the shared option is silently shadowed.
        foreach (var option in command.Options.Where(o => ShortBindings(o).Any(b => b.Alias == alias)))
            Assert.Equal(option.Name == winner, parsed.GetResult(option) is OptionResult { Implicit: false });
    }

    // Single-dash names/aliases of an option, including options declared short-first (Name is single-dash).
    private static IEnumerable<(string Alias, string Long)> ShortBindings(Option option) =>
        option.Aliases.Append(option.Name).Distinct(StringComparer.Ordinal)
            .Where(a => a.Length > 1 && a[0] == '-' && a[1] != '-').Select(a => (a, LongName(option)));

    private static string LongName(Option option) =>
        option.Name.StartsWith("--", StringComparison.Ordinal) ? option.Name
        : option.Aliases.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) ?? option.Name;

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
