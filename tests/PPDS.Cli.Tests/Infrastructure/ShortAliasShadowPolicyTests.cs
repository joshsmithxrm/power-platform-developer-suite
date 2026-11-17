using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

// Synthetic commands only: construct and parse, never invoke actions.
[Trait("Category", "Unit")]
public sealed class ShortAliasShadowPolicyTests
{
    private static Option<bool> Owner(string name, string alias) => new(name, alias) { Description = "Command-specific option" };

    private static RootCommand Tree(Command leaf, Command? parent = null)
    {
        var root = new RootCommand();
        if (parent is null)
        {
            root.Subcommands.Add(leaf);
        }
        else
        {
            root.Subcommands.Add(parent);
            parent.Subcommands.Add(leaf);
        }

        return root;
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void QuietTwin_IsBuiltWhenCommandSpecificOptionOwnsMinusQ()
    {
        var other = Owner("--other", "-q");
        var leaf = new Command("leaf", "Leaf") { other };
        GlobalOptions.AddToCommand(leaf);
        var root = Tree(leaf);

        ShortAliasShadowPolicy.Apply(root);

        var quiet = Assert.Single(leaf.Options, o => o.Name == "--quiet");
        Assert.NotSame(GlobalOptions.Quiet, quiet);
        Assert.DoesNotContain("-q", quiet.Aliases);
        Assert.Contains("-q", GlobalOptions.Quiet.Aliases); // the shared static is untouched
        Assert.True(root.Parse("leaf -q").GetValue(other));
        var parsed = root.Parse("leaf --quiet");
        Assert.Empty(parsed.Errors);
        Assert.True(GlobalOptions.GetValues(parsed).Quiet);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void CsvCapableOutputFormatTwin_KeepsCsvAndResolvesThroughHelpers()
    {
        var force = Owner("--force", "-f");
        var leaf = new Command("leaf", "Leaf") { force };
        GlobalOptions.AddToCommand(leaf, supportsCsv: true);
        var root = Tree(leaf);

        ShortAliasShadowPolicy.Apply(root);

        var format = Assert.Single(leaf.Options, o => o.Name == "--output-format");
        Assert.NotSame(GlobalOptions.CsvCapableOutputFormat, format);
        Assert.DoesNotContain("-f", format.Aliases);
        Assert.True(root.Parse("leaf -f").GetValue(force));

        var csv = root.Parse("leaf --output-format csv");
        Assert.Empty(csv.Errors);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetValues(csv).OutputFormat);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetOutputFormat(csv.CommandResult));
        Assert.Equal(OutputFormat.Text, GlobalOptions.GetOutputFormat(root.Parse("leaf").CommandResult));
        Assert.Contains(root.Parse("leaf --output-format xml").Errors, e => e.Message.Contains("Invalid value 'xml'", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void OwnerRegisteredBeforeSharedOption_SharedAliasIsStripped()
    {
        var leaf = new Command("leaf", "Leaf") { Owner("--force", "-f") };
        GlobalOptions.AddToCommand(leaf);
        var root = Tree(leaf);
        var before = root.Parse("leaf -f");

        ShortAliasShadowPolicy.Apply(root);

        Assert.DoesNotContain("-f", Assert.Single(leaf.Options, o => o.Name == "--output-format").Aliases);
        Assert.Equal(Outcome(before), Outcome(root.Parse("leaf -f")));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void SharedOptionRegisteredBeforeOwner_IsLeftAloneSoTheGuardCanFail()
    {
        var leaf = new Command("leaf", "Leaf");
        GlobalOptions.AddToCommand(leaf);
        leaf.Options.Add(Owner("--force", "-f"));
        var root = Tree(leaf);
        var before = root.Parse("leaf -f json");

        ShortAliasShadowPolicy.Apply(root);

        var format = Assert.Single(leaf.Options, o => o.Name == "--output-format");
        Assert.Same(GlobalOptions.OutputFormat, format);
        Assert.Contains("-f", format.Aliases);
        Assert.Contains("-f", leaf.Options.Single(o => o.Name == "--force").Aliases);
        // Parsing is unchanged (the first-registered option keeps the alias).
        Assert.Equal(Outcome(before), Outcome(root.Parse("leaf -f json")));
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-11")]
    public void TwinLeafUnderParentCarryingSharedOptions_ReadsTheLeafsOutputFormat()
    {
        var parent = new Command("parent", "Parent");
        GlobalOptions.AddToCommand(parent); // static --output-format with an implicit Text default
        var leaf = new Command("leaf", "Leaf") { Owner("--force", "-f") };
        GlobalOptions.AddToCommand(leaf);
        var root = Tree(leaf, parent);

        ShortAliasShadowPolicy.Apply(root);

        Assert.NotSame(GlobalOptions.OutputFormat, Assert.Single(leaf.Options, o => o.Name == "--output-format"));
        var parsed = root.Parse("parent leaf --output-format json");
        Assert.Empty(parsed.Errors);
        Assert.Equal(OutputFormat.Json, GlobalOptions.GetValues(parsed).OutputFormat);
        Assert.True(GlobalOptions.GetValues(parsed).IsJsonMode);
    }

    private static string[] Outcome(ParseResult parsed) =>
        parsed.CommandResult.Children.OfType<OptionResult>().Where(r => !r.Implicit)
            .Select(r => r.Option.Name + "=" + r.GetValueOrDefault<object?>()).Order(StringComparer.Ordinal).ToArray();
}
