using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Choice;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class ChoiceCommandGroupTests
{
    private readonly Command _command;

    public ChoiceCommandGroupTests()
    {
        _command = ChoiceCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("choice", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("choice", _command.Description!.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasAllSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("create", names);
        Assert.Contains("update", names);
        Assert.Contains("delete", names);
        Assert.Contains("add-option", names);
        Assert.Contains("update-option", names);
        Assert.Contains("remove-option", names);
        Assert.Contains("reorder", names);
    }

    [Fact]
    public void Create_HasSevenSubcommands()
    {
        Assert.Equal(7, _command.Subcommands.Count);
    }
}

[Trait("Category", "Unit")]
public class ChoiceCreateCommandTests
{
    private readonly Command _command;

    public ChoiceCreateCommandTests()
    {
        _command = ChoiceCommandGroup.CreateCreateCommand();
    }

    [Fact]
    public void Create_HasCorrectName()
    {
        Assert.Equal("create", _command.Name);
    }

    [Fact]
    public void Create_HasRequiredSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasRequiredDisplayNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--display-name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasRequiredOptionsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--options");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_status --display-name \"Status\" --options \"Active=1,Inactive=2\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_status --display-name \"Status\" --options \"Active=1,Inactive=2\" --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ChoiceOptionParsingTests
{
    [Fact]
    public void ParseOptionDefinitions_ValidInput_ReturnsOptions()
    {
        var result = ChoiceCommandGroup.ParseOptionDefinitions("Active=1,Inactive=2,Pending=3");

        Assert.Equal(3, result.Length);
        Assert.Equal("Active", result[0].Label);
        Assert.Equal(1, result[0].Value);
        Assert.Equal("Inactive", result[1].Label);
        Assert.Equal(2, result[1].Value);
        Assert.Equal("Pending", result[2].Label);
        Assert.Equal(3, result[2].Value);
    }

    [Fact]
    public void ParseOptionDefinitions_WithSpaces_TrimsCorrectly()
    {
        var result = ChoiceCommandGroup.ParseOptionDefinitions(" Active = 1 , Inactive = 2 ");

        Assert.Equal(2, result.Length);
        Assert.Equal("Active", result[0].Label);
        Assert.Equal(1, result[0].Value);
    }

    [Fact]
    public void ParseOptionDefinitions_InvalidPairs_SkipsInvalid()
    {
        var result = ChoiceCommandGroup.ParseOptionDefinitions("Active=1,BadEntry,Inactive=2");

        Assert.Equal(2, result.Length);
        Assert.Equal("Active", result[0].Label);
        Assert.Equal("Inactive", result[1].Label);
    }

    [Fact]
    public void ParseOrder_ValidInput_ReturnsInts()
    {
        var result = ChoiceCommandGroup.ParseOrder("1,3,2,4");

        Assert.Equal(4, result.Length);
        Assert.Equal(1, result[0]);
        Assert.Equal(3, result[1]);
        Assert.Equal(2, result[2]);
        Assert.Equal(4, result[3]);
    }

    [Fact]
    public void ParseOrder_WithSpaces_TrimsCorrectly()
    {
        var result = ChoiceCommandGroup.ParseOrder(" 1 , 3 , 2 ");

        Assert.Equal(3, result.Length);
        Assert.Equal(1, result[0]);
        Assert.Equal(3, result[1]);
        Assert.Equal(2, result[2]);
    }
}

[Trait("Category", "Unit")]
public class ChoiceAddOptionCommandTests
{
    private readonly Command _command;

    public ChoiceAddOptionCommandTests()
    {
        _command = ChoiceCommandGroup.CreateAddOptionCommand();
    }

    [Fact]
    public void AddOption_HasCorrectName()
    {
        Assert.Equal("add-option", _command.Name);
    }

    [Fact]
    public void AddOption_HasRequiredSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void AddOption_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void AddOption_HasRequiredLabelOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--label");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void AddOption_HasOptionalValueOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--value");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void AddOption_HasOptionalColorOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--color");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_status --label \"New Status\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_status --label \"New Status\" --value 100 --color #FF0000");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ChoiceUpdateOptionCommandTests
{
    private readonly Command _command;

    public ChoiceUpdateOptionCommandTests()
    {
        _command = ChoiceCommandGroup.CreateUpdateOptionCommand();
    }

    [Fact]
    public void UpdateOption_HasCorrectName()
    {
        Assert.Equal("update-option", _command.Name);
    }

    [Fact]
    public void UpdateOption_HasRequiredValueOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--value");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void UpdateOption_HasRequiredLabelOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--label");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_status --value 1 --label \"Updated Label\"");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ChoiceRemoveOptionCommandTests
{
    private readonly Command _command;

    public ChoiceRemoveOptionCommandTests()
    {
        _command = ChoiceCommandGroup.CreateRemoveOptionCommand();
    }

    [Fact]
    public void RemoveOption_HasCorrectName()
    {
        Assert.Equal("remove-option", _command.Name);
    }

    [Fact]
    public void RemoveOption_HasRequiredValueOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--value");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void RemoveOption_HasOptionalForceOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_status --value 1");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForce_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_status --value 1 --force");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ChoiceReorderCommandTests
{
    private readonly Command _command;

    public ChoiceReorderCommandTests()
    {
        _command = ChoiceCommandGroup.CreateReorderCommand();
    }

    [Fact]
    public void Reorder_HasCorrectName()
    {
        Assert.Equal("reorder", _command.Name);
    }

    [Fact]
    public void Reorder_HasRequiredOrderOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--order");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_status --order \"1,3,2,4\"");
        Assert.Empty(result.Errors);
    }
}
