using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class ColumnCommandGroupTests
{
    private readonly Command _command;

    public ColumnCommandGroupTests()
    {
        _command = ColumnCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("column", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("column", _command.Description!.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasAllSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("create", names);
        Assert.Contains("update", names);
        Assert.Contains("delete", names);
    }

    [Fact]
    public void Create_HasExactlyThreeSubcommands()
    {
        Assert.Equal(3, _command.Subcommands.Count);
    }
}

[Trait("Category", "Unit")]
public class ColumnCreateCommandTests
{
    private readonly Command _command;

    public ColumnCreateCommandTests()
    {
        _command = ColumnCommandGroup.CreateCreateCommand();
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
    public void Create_HasRequiredEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");
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
    public void Create_HasRequiredTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasTypeSpecificOptions()
    {
        var optionNames = _command.Options.Select(o => o.Name).ToList();

        Assert.Contains("--max-length", optionNames);
        Assert.Contains("--min-value", optionNames);
        Assert.Contains("--max-value", optionNames);
        Assert.Contains("--precision", optionNames);
        Assert.Contains("--format", optionNames);
        Assert.Contains("--date-time-behavior", optionNames);
        Assert.Contains("--option-set-name", optionNames);
        Assert.Contains("--options", optionNames);
        Assert.Contains("--default-value", optionNames);
        Assert.Contains("--true-label", optionNames);
        Assert.Contains("--false-label", optionNames);
        Assert.Contains("--max-size-kb", optionNames);
    }

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyColumn --display-name \"My Column\" --type String");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithBooleanType_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyBool --display-name \"My Bool\" --type Boolean --true-label Yes --false-label No");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMissingType_HasErrors()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyColumn --display-name \"My Column\"");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyColumn --display-name \"My Column\" --type Integer --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ColumnOptionParsingTests
{
    [Fact]
    public void ParseOptionDefinitions_ValidInput_ReturnsOptions()
    {
        var result = ColumnCommandGroup.ParseOptionDefinitions("Active=1,Inactive=2");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Length);
        Assert.Equal("Active", result[0].Label);
        Assert.Equal(1, result[0].Value);
        Assert.Equal("Inactive", result[1].Label);
        Assert.Equal(2, result[1].Value);
    }

    [Fact]
    public void ParseOptionDefinitions_NullInput_ReturnsNull()
    {
        var result = ColumnCommandGroup.ParseOptionDefinitions(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseOptionDefinitions_EmptyInput_ReturnsNull()
    {
        var result = ColumnCommandGroup.ParseOptionDefinitions("");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOptionDefinitions_WhitespaceInput_ReturnsNull()
    {
        var result = ColumnCommandGroup.ParseOptionDefinitions("   ");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOptionDefinitions_WithSpaces_TrimsCorrectly()
    {
        var result = ColumnCommandGroup.ParseOptionDefinitions(" Active = 1 , Inactive = 2 ");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Length);
        Assert.Equal("Active", result[0].Label);
        Assert.Equal(1, result[0].Value);
    }
}

[Trait("Category", "Unit")]
public class ColumnUpdateCommandTests
{
    private readonly Command _command;

    public ColumnUpdateCommandTests()
    {
        _command = ColumnCommandGroup.CreateUpdateCommand();
    }

    [Fact]
    public void Update_HasCorrectName()
    {
        Assert.Equal("update", _command.Name);
    }

    [Fact]
    public void Update_HasRequiredSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Update_HasRequiredEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Update_HasRequiredColumnOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--column");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --column new_mycolumn");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --column new_mycolumn --display-name \"Updated\" --description \"desc\" --required-level Required --max-length 200 --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class ColumnDeleteCommandTests
{
    private readonly Command _command;

    public ColumnDeleteCommandTests()
    {
        _command = ColumnCommandGroup.CreateDeleteCommand();
    }

    [Fact]
    public void Delete_HasCorrectName()
    {
        Assert.Equal("delete", _command.Name);
    }

    [Fact]
    public void Delete_HasRequiredSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Delete_HasRequiredEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Delete_HasRequiredColumnOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--column");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Delete_HasOptionalForceOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --column new_mycolumn");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForceAndDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --column new_mycolumn --force --dry-run");
        Assert.Empty(result.Errors);
    }
}
