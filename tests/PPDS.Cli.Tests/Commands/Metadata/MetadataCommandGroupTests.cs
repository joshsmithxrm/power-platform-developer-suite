using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

public class MetadataCommandGroupTests
{
    private readonly Command _command;

    public MetadataCommandGroupTests()
    {
        _command = MetadataCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("metadata", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("metadata", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasAllSubcommands()
    {
        var subcommandNames = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("entities", subcommandNames);
        Assert.Contains("entity", subcommandNames);
        Assert.Contains("attributes", subcommandNames);
        Assert.Contains("relationships", subcommandNames);
        Assert.Contains("keys", subcommandNames);
        Assert.Contains("optionsets", subcommandNames);
        Assert.Contains("optionset", subcommandNames);
    }

    [Fact]
    public void ProfileOption_IsOptional()
    {
        Assert.False(MetadataCommandGroup.ProfileOption.Required);
    }

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", MetadataCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasAlias()
    {
        Assert.Contains("-p", MetadataCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_IsOptional()
    {
        Assert.False(MetadataCommandGroup.EnvironmentOption.Required);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", MetadataCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasAlias()
    {
        Assert.Contains("-env", MetadataCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}

public class EntitiesCommandTests
{
    private readonly Command _command;

    public EntitiesCommandTests()
    {
        _command = EntitiesCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("entities", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("List", _command.Description);
    }

    [Fact]
    public void Create_HasOptionalCustomOnlyOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--custom-only");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalFilterOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Parse_WithNoOptions_Succeeds()
    {
        var result = _command.Parse("");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithCustomOnly_Succeeds()
    {
        var result = _command.Parse("--custom-only");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithFilter_Succeeds()
    {
        var result = _command.Parse("--filter new_*");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("--profile dev --environment Dev --custom-only --filter new_*");
        Assert.Empty(result.Errors);
    }
}

public class EntityCommandTests
{
    private readonly Command _command;

    public EntityCommandTests()
    {
        _command = EntityCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("entity", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("metadata", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasEntityArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "entity");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_HasOptionalIncludeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--include");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Parse_WithEntityArgument_Succeeds()
    {
        var result = _command.Parse("account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithInclude_Succeeds()
    {
        var result = _command.Parse("account --include attributes,relationships");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("account --profile dev --include attributes --include keys");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutEntityArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }
}

public class AttributesCommandTests
{
    private readonly Command _command;

    public AttributesCommandTests()
    {
        _command = AttributesCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("attributes", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("attributes", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasEntityArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "entity");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_HasOptionalTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Parse_WithEntityArgument_Succeeds()
    {
        var result = _command.Parse("account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithType_Succeeds()
    {
        var result = _command.Parse("account --type Lookup");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("account --profile dev --type String");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutEntityArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }
}

public class RelationshipsCommandTests
{
    private readonly Command _command;

    public RelationshipsCommandTests()
    {
        _command = RelationshipsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("relationships", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("relationship", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasEntityArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "entity");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_HasOptionalTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Parse_WithEntityArgument_Succeeds()
    {
        var result = _command.Parse("account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithType_Succeeds()
    {
        var result = _command.Parse("account --type OneToMany");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("account --profile dev --type ManyToOne");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutEntityArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }
}

public class OptionSetsCommandTests
{
    private readonly Command _command;

    public OptionSetsCommandTests()
    {
        _command = OptionSetsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("optionsets", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("option set", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasOptionalFilterOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Parse_WithNoOptions_Succeeds()
    {
        var result = _command.Parse("");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithFilter_Succeeds()
    {
        var result = _command.Parse("--filter new_*");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("--profile dev --filter new_*");
        Assert.Empty(result.Errors);
    }
}

public class OptionSetCommandTests
{
    private readonly Command _command;

    public OptionSetCommandTests()
    {
        _command = OptionSetCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("optionset", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("option set", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasNameArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "name");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Parse_WithNameArgument_Succeeds()
    {
        var result = _command.Parse("new_customstatus");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("new_customstatus --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutNameArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }
}

public class KeysCommandTests
{
    private readonly Command _command;

    public KeysCommandTests()
    {
        _command = KeysCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("keys", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("key", _command.Description?.ToLowerInvariant());
    }

    [Fact]
    public void Create_HasEntityArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "entity");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Parse_WithEntityArgument_Succeeds()
    {
        var result = _command.Parse("account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse("account --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutEntityArgument_HasErrors()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }
}
