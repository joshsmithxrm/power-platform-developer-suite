using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

/// <summary>
/// Parse-level tests for 'ppds webresources update' (#1207). Covers AC-WR-59.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateCommandTests
{
    private readonly Command _command = UpdateCommand.Create();

    [Fact]
    public void Create_CommandNameIsUpdate()
    {
        Assert.Equal("update", _command.Name);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("file")]
    public void Create_HasArgument(string argumentName)
    {
        Assert.NotNull(_command.Arguments.FirstOrDefault(a => a.Name == argumentName));
    }

    [Fact]
    public void Create_HasPublishOption()
    {
        Assert.NotNull(_command.Options.FirstOrDefault(o => o.Name == "--publish"));
    }

    [Fact]
    public void Parse_ValidArgs_HasNoErrors()
    {
        var result = _command.Parse("hsl_vet_icon.svg icon.svg --publish");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingFile_HasErrors()
    {
        var result = _command.Parse("hsl_vet_icon.svg");
        Assert.NotEmpty(result.Errors);
    }
}
