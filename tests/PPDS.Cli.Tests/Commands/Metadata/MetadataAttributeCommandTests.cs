using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Attribute;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers AC-39: canonical 'attribute' command has create/update/delete subcommands.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAttributeCommandTests
{
    private readonly Command _command;

    public MetadataAttributeCommandTests()
    {
        _command = AttributeCommandGroup.Create();
    }

    [Fact]
    public void Create_CommandNameIsAttribute()
    {
        Assert.Equal("attribute", _command.Name);
    }

    [Fact]
    public void Create_HasCreateSubcommand()
    {
        Assert.Contains("create", _command.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void Create_HasUpdateSubcommand()
    {
        Assert.Contains("update", _command.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void Create_HasDeleteSubcommand()
    {
        Assert.Contains("delete", _command.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void Create_HasLocalOptionSubcommands()
    {
        // #1161: attribute hosts create/update/delete plus local-option add/update/remove.
        var names = _command.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("add-option", names);
        Assert.Contains("update-option", names);
        Assert.Contains("remove-option", names);
        Assert.Equal(6, _command.Subcommands.Count);
    }
}

[Trait("Category", "Unit")]
public class AttributeCreateCommandTests
{
    private readonly Command _command;

    public AttributeCreateCommandTests()
    {
        _command = AttributeCommandGroup.CreateCreateCommand();
    }

    [Fact]
    public void Create_HasCorrectName()
    {
        Assert.Equal("create", _command.Name);
    }

    [Fact]
    public void Create_HasRequiredSolutionOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Create_HasRequiredEntityOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Create_HasRequiredNameOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Create_HasRequiredDisplayNameOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--display-name");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Create_HasRequiredTypeOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Create_HasOptionalOptionsOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--options");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySol --entity account --name new_col --display-name \"My Col\" --type String");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMissingEntity_HasErrors()
    {
        var result = _command.Parse(
            "--solution MySol --name new_col --display-name \"My Col\" --type String");
        Assert.NotEmpty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class AttributeUpdateCommandTests
{
    private readonly Command _command;

    public AttributeUpdateCommandTests()
    {
        _command = AttributeCommandGroup.CreateUpdateCommand();
    }

    [Fact]
    public void Update_HasCorrectName()
    {
        Assert.Equal("update", _command.Name);
    }

    [Fact]
    public void Update_HasRequiredColumnOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--column");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySol --entity account --column new_col");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class AttributeDeleteCommandTests
{
    private readonly Command _command;

    public AttributeDeleteCommandTests()
    {
        _command = AttributeCommandGroup.CreateDeleteCommand();
    }

    [Fact]
    public void Delete_HasCorrectName()
    {
        Assert.Equal("delete", _command.Name);
    }

    [Fact]
    public void Delete_HasOptionalForceOption()
    {
        var opt = _command.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySol --entity account --column new_col");
        Assert.Empty(result.Errors);
    }
}
