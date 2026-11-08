using System.CommandLine;
using PPDS.Cli.Commands.Views;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Views;

public sealed class ViewReorderCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(",")]
    [InlineData(",name,parentaccountid.name")]
    [InlineData("name,parentaccountid.name,")]
    [InlineData("name,,parentaccountid.name")]
    [InlineData("name,  ,parentaccountid.name")]
    [InlineData("name,NAME")]
    [InlineData("name, name ")]
    [Trait("AC", "AC-39")]
    public void Columns_InvalidSyntax_IsParseError(string columns)
    {
        var parsed = ReorderColumnsCommand.Create().Parse(["--entity", "account", "--view", "Example", "--columns", columns]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    [Trait("AC", "AC-39")]
    public void Columns_CompleteSyntax_Parses()
    {
        var parsed = ReorderColumnsCommand.Create().Parse(
            ["--entity", "account", "--view", "Example", "--columns", "parentaccountid.name, NAME"]);
        Assert.Empty(parsed.Errors);
    }

    [Fact]
    [Trait("AC", "AC-39")]
    public void Help_ExplainsLosslessReorder()
    {
        var command = ReorderColumnsCommand.Create();
        var description = command.Description + " " + command.Options.Single(option => option.Name == "--columns").Description;
        Assert.Contains("every", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove-column", description);
        Assert.DoesNotContain("dropped", description);
    }
}
