using System.CommandLine;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

// Construct and parse only. No actions, Program.Main, profiles, or providers.
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class EnvironmentAliasPolicyTests
{
    [Fact]
    [Trait("AC", "AC-ALIAS-09")]
    public void Apply_IsIdempotentAndRetainsUnsupportedShortAlias()
    {
        var root = Program.CreateRootCommand(includeInternal: true);
        var before = Snapshot(root);
        EnvironmentAliasPolicy.Apply(root);
        EnvironmentAliasPolicy.Apply(root);
        Assert.Equal(before, Snapshot(root));

        var standalone = new RootCommand();
        var environment = new Option<string?>("--environment", "-z") { Description = "Environment" };
        var unrelated = new Option<string?>("--elsewhere", "-e") { Description = "Other value" };
        standalone.Options.Add(environment);
        standalone.Options.Add(unrelated);
        EnvironmentAliasPolicy.Apply(standalone);
        EnvironmentAliasPolicy.Apply(standalone);
        Assert.Equal(new[] { "-env", "-z" }, environment.Aliases.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("-e", environment.Aliases);
        Assert.Equal(new[] { "-e" }, unrelated.Aliases);
        Assert.Equal("Other value", unrelated.Description);
    }

    [Theory]
    [InlineData("https://entity.fixture.invalid")]
    [InlineData("account")]
    [Trait("AC", "AC-ALIAS-01")]
    [Trait("AC", "AC-ALIAS-04")]
    public void RealEntityValues_KeepTheirRoleRegardlessOfShape(string value)
    {
        var root = Program.CreateRootCommand(includeInternal: false);
        var parsed = root.Parse(["data", "delete", "-e", value, "--id", "11111111-1111-1111-1111-111111111111"]);
        Assert.Empty(parsed.Errors);
        var option = Assert.IsType<Option<string?>>(parsed.CommandResult.Command.Options.Single(o => o.Name == "--entity"));
        Assert.Equal(value, parsed.GetValue(option));
        AssertNotice(parsed);
    }

    [Theory]
    [InlineData("qa-short-name")]
    [InlineData("not-a-url")]
    [Trait("AC", "AC-ALIAS-05")]
    public void RealEnvironmentValues_DoNotBecomeEntityRoles(string value)
    {
        var root = Program.CreateRootCommand(includeInternal: false);
        var parsed = root.Parse(["views", "list", "--entity", "account", "-e", value]);
        Assert.Empty(parsed.Errors);
        var option = Assert.IsType<Option<string?>>(parsed.CommandResult.Command.Options.Single(o => o.Name == "--environment"));
        Assert.Equal(value, parsed.GetValue(option));
        AssertNoNotice(parsed);
    }

    [Theory]
    [InlineData("-e")]
    [InlineData("-e=account")]
    [InlineData("-env")]
    [Trait("AC", "AC-ALIAS-05")]
    public void OtherOptionLiteralValues_DoNotBecomeEntityOptionTokens(string value)
    {
        var root = EntityRoot(out var entity);
        var literal = new Option<string?>("--literal");
        root.Options.Add(literal);
        var parsed = root.Parse(["--entity", "account", "--literal=" + value]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("account", parsed.GetValue(entity));
        Assert.Equal(value, parsed.GetValue(literal));
        AssertNoNotice(parsed);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-05")]
    public void Terminator_KeepsLegacyLookingValuesAsArguments()
    {
        var root = EntityRoot(out _);
        var values = new Argument<string[]>("values") { Arity = ArgumentArity.ZeroOrMore };
        root.Arguments.Add(values);
        var parsed = root.Parse(["--", "-e", "account"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal(new[] { "-e", "account" }, parsed.GetValue(values));
        AssertNoNotice(parsed);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-04")]
    public void RecursiveEntityOption_IsRecognizedOnSelectedChild()
    {
        var root = EntityRoot(out var entity);
        entity.Recursive = true;
        root.Subcommands.Add(new Command("child"));
        var parsed = root.Parse(["child", "--entity", "account", "-e", "contact"]);
        Assert.NotNull(parsed.GetResult(entity));
        AssertNotice(parsed);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-04")]
    public void ParentLocalEntityOption_BindsItsActualIdentifierBeforeChild()
    {
        var root = EntityRoot(out var entity);
        root.Subcommands.Add(new Command("child"));
        var parsed = root.Parse(["-e", "account", "child"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("account", parsed.GetValue(entity));
        AssertNotice(parsed);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-05")]
    public void RepeatedLongOnlyEntityOptions_DoNotInventShortUse()
    {
        var root = EntityRoot(out var entity);
        var parsed = root.Parse(["--entity", "account", "--entity", "contact"]);
        Assert.NotNull(parsed.GetResult(entity));
        AssertNoNotice(parsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-ALIAS-04")]
    public void RepeatedListAliases_WarnOnceInEitherOrder(bool shortFirst)
    {
        var root = new RootCommand();
        var entities = new Option<string[]>("--entities", "-e") { AllowMultipleArgumentsPerToken = true };
        root.Options.Add(entities);
        EnvironmentAliasPolicy.Apply(root);
        var parsed = root.Parse(shortFirst
            ? ["-e", "account", "--entities", "contact"]
            : ["--entities", "account", "-e", "contact"]);
        Assert.NotNull(parsed.GetResult(entities));
        AssertNotice(parsed);
    }

    [Fact]
    [Trait("AC", "AC-ALIAS-05")]
    public void UnselectedEntityCommand_DoesNotChangeEnvironmentBinding()
    {
        var root = new RootCommand();
        var entityCommand = new Command("entity-command");
        entityCommand.Options.Add(new Option<string?>("--entity", "-e"));
        var environmentCommand = new Command("environment-command");
        var environment = new Option<string?>("--environment", "-e");
        environmentCommand.Options.Add(environment);
        root.Subcommands.Add(entityCommand);
        root.Subcommands.Add(environmentCommand);
        EnvironmentAliasPolicy.Apply(root);
        var parsed = root.Parse(["environment-command", "-e", "account"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("account", parsed.GetValue(environment));
        AssertNoNotice(parsed);
    }

    private static RootCommand EntityRoot(out Option<string?> entity)
    {
        var root = new RootCommand();
        entity = new Option<string?>("--entity", "-e");
        root.Options.Add(entity);
        EnvironmentAliasPolicy.Apply(root);
        return root;
    }

    private static string[] Snapshot(Command command) => command.Options
        .Select(option => option.Name + "|" + string.Join(',', option.Aliases.Order(StringComparer.Ordinal)) + "|" + option.Description)
        .Concat(command.Subcommands.SelectMany(Snapshot)).ToArray();

    private static void AssertNotice(ParseResult parsed)
    {
        using var writer = new StringWriter();
        EnvironmentAliasPolicy.WarnIfDeprecatedEntityAliasUsed(parsed, writer);
        Assert.Single(writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("deprecat", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--entity", writer.ToString());
        Assert.DoesNotContain("fixture.invalid", writer.ToString());
        Assert.DoesNotContain("account", writer.ToString());
        Assert.DoesNotContain("contact", writer.ToString());
    }

    private static void AssertNoNotice(ParseResult parsed)
    {
        using var writer = new StringWriter();
        EnvironmentAliasPolicy.WarnIfDeprecatedEntityAliasUsed(parsed, writer);
        Assert.Empty(writer.ToString());
    }
}
