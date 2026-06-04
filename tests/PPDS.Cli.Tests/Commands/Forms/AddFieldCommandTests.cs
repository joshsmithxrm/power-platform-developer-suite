using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using PPDS.Cli.Commands.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

public class AddFieldCommandTests
{
    private readonly Command _command;

    public AddFieldCommandTests()
    {
        _command = AddFieldCommand.Create();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasCorrectName()
    {
        Assert.Equal("add-field", _command.Name);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.NotEmpty(_command.Description);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_RequiresEntityFormSection()
    {
        var entityOption = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        var formOption = _command.Options.FirstOrDefault(o => o.Name == "--form");
        var sectionOption = _command.Options.FirstOrDefault(o => o.Name == "--section");

        Assert.NotNull(entityOption);
        Assert.True(entityOption.Required);

        Assert.NotNull(formOption);
        Assert.True(formOption.Required);

        Assert.NotNull(sectionOption);
        Assert.True(sectionOption.Required);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasFieldOption()
    {
        var fieldOption = _command.Options.FirstOrDefault(o => o.Name == "--field");
        Assert.NotNull(fieldOption);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_NoClassidOption()
    {
        _command.Options.Should().NotContain(o => o.Name == "--classid");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithEntityFormSectionAndField_Succeeds()
    {
        var result = _command.Parse("--entity account --form \"Main\" --section \"General\" --field name");
        Assert.Empty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithMultipleFields_Succeeds()
    {
        var result = _command.Parse("--entity account --form \"Main\" --section \"General\" --field name --field createdon");
        Assert.Empty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutEntity_HasErrors()
    {
        var result = _command.Parse("--form \"Main\" --section \"General\" --field name");
        Assert.NotEmpty(result.Errors);
    }
}
