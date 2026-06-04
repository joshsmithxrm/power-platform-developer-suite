using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

public class AddSubgridCommandTests
{
    private readonly Command _command;

    public AddSubgridCommandTests()
    {
        _command = AddSubgridCommand.Create();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasCorrectName()
    {
        Assert.Equal("add-subgrid", _command.Name);
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
    public void Command_RequiresTargetEntityAndDefaultView()
    {
        var targetEntityOption = _command.Options.FirstOrDefault(o => o.Name == "--target-entity");
        var defaultViewOption = _command.Options.FirstOrDefault(o => o.Name == "--default-view");

        Assert.NotNull(targetEntityOption);
        Assert.True(targetEntityOption.Required);

        Assert.NotNull(defaultViewOption);
        Assert.True(defaultViewOption.Required);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_RelationshipIsOptional()
    {
        var relationshipOption = _command.Options.FirstOrDefault(o => o.Name == "--relationship");

        Assert.NotNull(relationshipOption);
        Assert.False(relationshipOption.Required);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithAllRequired_Succeeds()
    {
        var result = _command.Parse(
            "--entity account --form \"Main\" --section \"General\" --label \"Contacts\" " +
            "--target-entity contact --default-view 00000000-0000-0000-0000-000000000001");
        Assert.Empty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutTargetEntity_HasErrors()
    {
        var result = _command.Parse(
            "--entity account --form \"Main\" --section \"General\" --label \"Contacts\" " +
            "--default-view 00000000-0000-0000-0000-000000000001");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutDefaultView_HasErrors()
    {
        var result = _command.Parse(
            "--entity account --form \"Main\" --section \"General\" --label \"Contacts\" " +
            "--target-entity contact");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithRelationship_Succeeds()
    {
        var result = _command.Parse(
            "--entity account --form \"Main\" --section \"General\" --label \"Contacts\" " +
            "--target-entity contact --default-view 00000000-0000-0000-0000-000000000001 " +
            "--relationship account_contacts");
        Assert.Empty(result.Errors);
    }
}
