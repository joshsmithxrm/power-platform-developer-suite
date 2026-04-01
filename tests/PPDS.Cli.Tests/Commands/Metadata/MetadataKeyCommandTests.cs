using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Key;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class KeyCommandGroupTests
{
    private readonly Command _command;

    public KeyCommandGroupTests()
    {
        _command = KeyCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("key", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("key", _command.Description!.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasAllSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("create", names);
        Assert.Contains("delete", names);
        Assert.Contains("reactivate", names);
    }

    [Fact]
    public void Create_HasExactlyThreeSubcommands()
    {
        Assert.Equal(3, _command.Subcommands.Count);
    }
}

[Trait("Category", "Unit")]
public class KeyCreateCommandTests
{
    private readonly Command _command;

    public KeyCreateCommandTests()
    {
        _command = KeyCommandGroup.CreateCreateCommand();
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
    public void Create_HasRequiredAttributesOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--attributes");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyKey --display-name \"My Key\" --attributes \"attr1,attr2\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --entity new_mytable --name new_MyKey --display-name \"My Key\" --attributes \"attr1\" --dry-run");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMissingSolution_HasErrors()
    {
        var result = _command.Parse(
            "--entity new_mytable --name new_MyKey --display-name \"My Key\" --attributes \"attr1\"");
        Assert.NotEmpty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class KeyAttributeParsingTests
{
    [Fact]
    public void ParseAttributes_ValidInput_ReturnsArray()
    {
        var result = KeyCommandGroup.ParseAttributes("attr1,attr2,attr3");

        Assert.Equal(3, result.Length);
        Assert.Equal("attr1", result[0]);
        Assert.Equal("attr2", result[1]);
        Assert.Equal("attr3", result[2]);
    }

    [Fact]
    public void ParseAttributes_WithSpaces_TrimsCorrectly()
    {
        var result = KeyCommandGroup.ParseAttributes(" attr1 , attr2 ");

        Assert.Equal(2, result.Length);
        Assert.Equal("attr1", result[0]);
        Assert.Equal("attr2", result[1]);
    }

    [Fact]
    public void ParseAttributes_SingleAttribute_ReturnsSingleElement()
    {
        var result = KeyCommandGroup.ParseAttributes("attr1");

        Assert.Single(result);
        Assert.Equal("attr1", result[0]);
    }

    [Fact]
    public void ParseAttributes_EmptySegments_Skipped()
    {
        var result = KeyCommandGroup.ParseAttributes("attr1,,attr2");

        Assert.Equal(2, result.Length);
        Assert.Equal("attr1", result[0]);
        Assert.Equal("attr2", result[1]);
    }
}

[Trait("Category", "Unit")]
public class KeyDeleteCommandTests
{
    private readonly Command _command;

    public KeyDeleteCommandTests()
    {
        _command = KeyCommandGroup.CreateDeleteCommand();
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
    public void Delete_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
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
        var result = _command.Parse("--solution MySolution --entity new_mytable --name new_mykey");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForceAndDryRun_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --name new_mykey --force --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class KeyReactivateCommandTests
{
    private readonly Command _command;

    public KeyReactivateCommandTests()
    {
        _command = KeyCommandGroup.CreateReactivateCommand();
    }

    [Fact]
    public void Reactivate_HasCorrectName()
    {
        Assert.Equal("reactivate", _command.Name);
    }

    [Fact]
    public void Reactivate_HasRequiredSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Reactivate_HasRequiredEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Reactivate_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --entity new_mytable --name new_mykey");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMissingEntity_HasErrors()
    {
        var result = _command.Parse("--solution MySolution --name new_mykey");
        Assert.NotEmpty(result.Errors);
    }
}
