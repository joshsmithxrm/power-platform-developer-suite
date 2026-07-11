using System.CommandLine;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="CommandAliasDeprecation"/> using synthetic command trees, decoupled
/// from the four real #1246 renames (which have their own coverage in the per-group
/// *CommandGroupTests.cs files). Proves the mechanism is generic: it reacts to any
/// <c>Command.Aliases</c> registration, not a hardcoded name list.
/// </summary>
/// <remarks>
/// Uses the <see cref="TextWriter"/> overload (an isolated <see cref="StringWriter"/> per test)
/// rather than swapping <see cref="Console.Error"/>, which is unsafe under xUnit's default
/// cross-class test parallelization.
/// </remarks>
public sealed class CommandAliasDeprecationTests
{
    private static RootCommand BuildRoot()
    {
        var aliased = new Command("new-name", "A command with a deprecated alias");
        aliased.Aliases.Add("old-name");
        aliased.Subcommands.Add(new Command("sub", "A nested subcommand"));

        var plain = new Command("plain", "A command with no alias");
        plain.Subcommands.Add(new Command("sub", "A nested subcommand"));

        return new RootCommand { aliased, plain };
    }

    [Fact]
    public void TopLevelOldName_EmitsWarning()
    {
        var root = BuildRoot();
        var parseResult = root.Parse(["old-name"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal("warning: 'old-name' is deprecated; use 'new-name'" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void TopLevelNewName_EmitsNothing()
    {
        var root = BuildRoot();
        var parseResult = root.Parse(["new-name"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void NestedSubcommand_ViaOldName_EmitsWarning()
    {
        // Deprecation applies even when the deprecated name is not the innermost matched
        // command (e.g. `ppds plugintraces list` -- "list" is innermost, "plugintraces" is not).
        var root = BuildRoot();
        var parseResult = root.Parse(["old-name", "sub"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal("warning: 'old-name' is deprecated; use 'new-name'" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void NestedSubcommand_ViaNewName_EmitsNothing()
    {
        var root = BuildRoot();
        var parseResult = root.Parse(["new-name", "sub"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void CommandWithNoRegisteredAlias_NeverWarns()
    {
        var root = BuildRoot();
        var parseResult = root.Parse(["plain", "sub"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void UnmatchedTopLevelToken_DoesNotThrow()
    {
        var root = BuildRoot();
        var parseResult = root.Parse(["does-not-exist"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }
}
