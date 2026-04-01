using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Relationship;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
public class RelationshipCommandGroupTests
{
    private readonly Command _command;

    public RelationshipCommandGroupTests()
    {
        _command = RelationshipCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("relationship", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.Contains("relationship", _command.Description!.ToLowerInvariant());
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
public class RelationshipCreateCommandTests
{
    private readonly Command _command;

    public RelationshipCreateCommandTests()
    {
        _command = RelationshipCommandGroup.CreateCreateCommand();
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
    public void Create_HasRequiredFromOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--from");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasRequiredToOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--to");
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
    public void Create_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasOptionalCascadeOptions()
    {
        var optionNames = _command.Options.Select(o => o.Name).ToList();
        Assert.Contains("--cascade-delete", optionNames);
        Assert.Contains("--cascade-assign", optionNames);
    }

    [Fact]
    public void Create_HasOptionalLookupOptions()
    {
        var optionNames = _command.Options.Select(o => o.Name).ToList();
        Assert.Contains("--lookup-name", optionNames);
        Assert.Contains("--lookup-display-name", optionNames);
    }

    [Fact]
    public void Parse_OneToMany_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --from account --to contact --type one-to-many --name new_account_contact");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ManyToMany_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --from account --to contact --type many-to-many --name new_account_contact_mm");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_InvalidType_HasErrors()
    {
        var result = _command.Parse(
            "--solution MySolution --from account --to contact --type one-to-one --name new_rel");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithCascadeOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --from account --to contact --type one-to-many --name new_rel --cascade-delete Restrict --cascade-assign NoCascade");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --from account --to contact --type one-to-many --name new_rel --dry-run");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class RelationshipUpdateCommandTests
{
    private readonly Command _command;

    public RelationshipUpdateCommandTests()
    {
        _command = RelationshipCommandGroup.CreateUpdateCommand();
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
    public void Update_HasRequiredNameOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--name");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Update_HasCascadeOptions()
    {
        var optionNames = _command.Options.Select(o => o.Name).ToList();
        Assert.Contains("--cascade-delete", optionNames);
        Assert.Contains("--cascade-assign", optionNames);
        Assert.Contains("--cascade-merge", optionNames);
        Assert.Contains("--cascade-reparent", optionNames);
        Assert.Contains("--cascade-share", optionNames);
        Assert.Contains("--cascade-unshare", optionNames);
    }

    [Fact]
    public void Parse_WithRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_account_contact");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithCascadeOptions_Succeeds()
    {
        var result = _command.Parse(
            "--solution MySolution --name new_account_contact --cascade-delete Restrict --cascade-assign Cascade");
        Assert.Empty(result.Errors);
    }
}

[Trait("Category", "Unit")]
public class RelationshipDeleteCommandTests
{
    private readonly Command _command;

    public RelationshipDeleteCommandTests()
    {
        _command = RelationshipCommandGroup.CreateDeleteCommand();
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
        var result = _command.Parse("--solution MySolution --name new_account_contact");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithForce_Succeeds()
    {
        var result = _command.Parse("--solution MySolution --name new_account_contact --force");
        Assert.Empty(result.Errors);
    }
}
