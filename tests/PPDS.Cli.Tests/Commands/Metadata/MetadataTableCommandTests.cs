using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Table;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class TableCommandGroupTests
{
    private readonly Command _command;

    public TableCommandGroupTests()
    {
        _command = TableCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("table", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("table", _command.Description!.ToLowerInvariant());
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
public class TableCreateCommandTests
{
    private readonly Command _command;

    public TableCreateCommandTests()
    {
        _command = TableCommandGroup.CreateCreateCommand();
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
    public void Create_HasRequiredPluralNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--plural-name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasRequiredOwnershipOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--ownership");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasOptionalDescriptionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--description");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasOptionalDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_MyTable --display-name \"My Table\" --plural-name \"My Tables\" --ownership UserOwned");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMissingSolution_HasErrors()
    {
        var result = _command.Parse("--name new_MyTable --display-name \"My Table\" --plural-name \"My Tables\" --ownership UserOwned");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithInvalidOwnership_HasErrors()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_MyTable --display-name \"My Table\" --plural-name \"My Tables\" --ownership Invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_MyTable --display-name \"My Table\" --plural-name \"My Tables\" --ownership OrganizationOwned --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class TableUpdateCommandTests
{
    private readonly Command _command;

    public TableUpdateCommandTests()
    {
        _command = TableCommandGroup.CreateUpdateCommand();
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
    public void Update_HasOptionalDisplayNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--display-name");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --display-name \"Updated Name\" --plural-name \"Updated Names\" --description \"desc\" --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class TableDeleteCommandTests
{
    private readonly Command _command;

    public TableDeleteCommandTests()
    {
        _command = TableCommandGroup.CreateDeleteCommand();
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
    public void Delete_HasOptionalForceOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Delete_HasOptionalDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForce_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --dry-run");
        Assert.Empty(result.Errors);
    }
}
