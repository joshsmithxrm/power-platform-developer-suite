using System.CommandLine;
using PPDS.Cli.Commands.Users;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Users;

public class UsersCommandGroupTests
{
    private readonly Command _command;

    public UsersCommandGroupTests()
    {
        _command = UsersCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("users", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("user", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasShowSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "show");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasRolesSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "roles");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasThreeSubcommands()
    {
        Assert.Equal(3, _command.Subcommands.Count);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", UsersCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", UsersCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", UsersCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", UsersCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion

    #region L1 Regression — no duplicate short aliases

    [Fact]
    public void ListSubcommand_NoTwoOptions_ShareAShortAlias()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var aliases = listCommand.Options
            .SelectMany(o => o.Aliases)
            .Where(a => a.StartsWith("-") && !a.StartsWith("--"))
            .ToList();
        var duplicates = aliases.GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void ListSubcommand_FilterOption_HasNoFShortAlias()
    {
        // L1: -f is reserved for --output-format; --filter uses long form only.
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var filterOption = listCommand.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(filterOption);
        Assert.DoesNotContain("-f", filterOption!.Aliases);
    }

    #endregion
}
