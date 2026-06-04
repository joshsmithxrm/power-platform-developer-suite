using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using PPDS.Cli.Commands.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

public class FormsCommandGroupTests
{
    private readonly Command _command;

    public FormsCommandGroupTests()
    {
        _command = FormsCommandGroup.Create();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_ReturnsCommandWithCorrectName()
    {
        _command.Name.Should().Be("forms");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasDescription()
    {
        _command.Description.Should().NotBeNullOrEmpty();
    }

    /// <summary>AC-28: All expected subcommands are present.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasAllSubcommands()
    {
        var subcommandNames = _command.Subcommands.Select(c => c.Name).ToList();

        subcommandNames.Should().Contain("list");
        subcommandNames.Should().Contain("get");
        subcommandNames.Should().Contain("set-xml");
        subcommandNames.Should().Contain("add-tab");
        subcommandNames.Should().Contain("update-tab");
        subcommandNames.Should().Contain("remove-tab");
        subcommandNames.Should().Contain("find-tab");
        subcommandNames.Should().Contain("add-section");
        subcommandNames.Should().Contain("update-section");
        subcommandNames.Should().Contain("remove-section");
        subcommandNames.Should().Contain("find-section");
        subcommandNames.Should().Contain("add-field");
        subcommandNames.Should().Contain("remove-field");
        subcommandNames.Should().Contain("reorder-fields");
        subcommandNames.Should().Contain("add-subgrid");
        subcommandNames.Should().Contain("remove-subgrid");
        subcommandNames.Should().HaveCount(16);
    }

    /// <summary>AC-29: Every subcommand has a non-empty description.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AllSubcommands_HaveDescriptions()
    {
        foreach (var subcommand in _command.Subcommands)
        {
            subcommand.Description.Should().NotBeNullOrEmpty(
                because: $"subcommand '{subcommand.Name}' should have a description");
        }
    }
}

public class FormsListCommandTests
{
    private readonly Command _command;

    public FormsListCommandTests()
    {
        _command = ListCommand.Create();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasCorrectName()
    {
        _command.Name.Should().Be("list");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_RequiresEntityOption()
    {
        var entityOption = _command.Options.FirstOrDefault(o => o.Name == "--entity");

        entityOption.Should().NotBeNull();
        entityOption!.Required.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithEntity_Succeeds()
    {
        var result = _command.Parse("--entity account");

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutEntity_HasErrors()
    {
        var result = _command.Parse("");

        result.Errors.Should().NotBeEmpty();
    }
}

public class FormsGetCommandTests
{
    private readonly Command _command;

    public FormsGetCommandTests()
    {
        _command = GetCommand.Create();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_HasCorrectName()
    {
        _command.Name.Should().Be("get");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_RequiresEntityAndFormOptions()
    {
        var entityOption = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        var formOption = _command.Options.FirstOrDefault(o => o.Name == "--form");

        entityOption.Should().NotBeNull();
        entityOption!.Required.Should().BeTrue();

        formOption.Should().NotBeNull();
        formOption!.Required.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithEntityAndForm_Succeeds()
    {
        var result = _command.Parse("--entity account --form \"Account Main Form\"");

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutForm_HasErrors()
    {
        var result = _command.Parse("--entity account");

        result.Errors.Should().NotBeEmpty();
    }
}
