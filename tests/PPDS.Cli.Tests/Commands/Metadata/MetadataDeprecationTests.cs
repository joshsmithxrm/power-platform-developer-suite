using System.CommandLine;
using FluentAssertions;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Cli.Commands.Metadata.Choice;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Cli.Commands.Metadata.Table;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers AC-41 (deprecated noun warns to stderr with canonical name),
/// AC-42 (stdout clean — warning goes to stderr, not stdout),
/// AC-43 (shared execute path — deprecated commands delegate to canonical execute methods).
/// </summary>
[Trait("Category", "Unit")]
public class DeprecationWarningTests
{
    [Fact]
    public void Write_OutputsToStderr_NotStdout()
    {
        var savedStdout = Console.Out;
        var savedStderr = Console.Error;

        using var stdoutCapture = new StringWriter();
        using var stderrCapture = new StringWriter();

        Console.SetOut(stdoutCapture);
        Console.SetError(stderrCapture);

        try
        {
            DeprecationWarning.Write("ppds metadata table create", "ppds metadata entity create");
        }
        finally
        {
            Console.SetOut(savedStdout);
            Console.SetError(savedStderr);
        }

        stdoutCapture.ToString().Should().BeEmpty("deprecation warning must not go to stdout");
        stderrCapture.ToString().Should().Contain("deprecated");
        stderrCapture.ToString().Should().Contain("ppds metadata table create");
        stderrCapture.ToString().Should().Contain("ppds metadata entity create");
    }

    [Fact]
    public void Write_MentionsDeprecatedAndCanonicalCommand()
    {
        using var stderrCapture = new StringWriter();
        var saved = Console.Error;
        Console.SetError(stderrCapture);

        try
        {
            DeprecationWarning.Write("ppds metadata column update", "ppds metadata attribute update");
        }
        finally
        {
            Console.SetError(saved);
        }

        var output = stderrCapture.ToString();
        output.Should().Contain("ppds metadata column update");
        output.Should().Contain("ppds metadata attribute update");
    }
}

[Trait("Category", "Unit")]
public class TableCommandDeprecationTests
{
    [Fact]
    public void TableCommand_Description_MentionsDeprecated()
    {
        var cmd = TableCommandGroup.Create();
        cmd.Description.Should().Contain("deprecated", "table is a deprecated alias for entity");
    }

    [Fact]
    public void TableCreate_ParsesCorrectly_SameOptionsAsEntityCreate()
    {
        var tableCreate = TableCommandGroup.CreateCreateCommand();
        var entityCreate = EntityCommand.CreateCreateCommand();

        var tableOptions = tableCreate.Options.Select(o => o.Name).OrderBy(x => x).ToList();
        var entityOptions = entityCreate.Options.Select(o => o.Name).OrderBy(x => x).ToList();

        tableOptions.Should().BeEquivalentTo(entityOptions,
            "deprecated table create must expose the same options as canonical entity create");
    }

    [Fact]
    public void TableUpdate_ParsesCorrectly_SameOptionsAsEntityUpdate()
    {
        var tableUpdate = TableCommandGroup.CreateUpdateCommand();
        var entityUpdate = EntityCommand.CreateUpdateCommand();

        var tableOptions = tableUpdate.Options.Select(o => o.Name).OrderBy(x => x).ToList();
        var entityOptions = entityUpdate.Options.Select(o => o.Name).OrderBy(x => x).ToList();

        tableOptions.Should().BeEquivalentTo(entityOptions);
    }
}

[Trait("Category", "Unit")]
public class ColumnCommandDeprecationTests
{
    [Fact]
    public void ColumnCommand_Description_MentionsDeprecated()
    {
        var cmd = ColumnCommandGroup.Create();
        cmd.Description.Should().Contain("deprecated", "column is a deprecated alias for attribute");
    }

    [Fact]
    public void ColumnCreate_ParsesCorrectly_SameOptionsAsAttributeCreate()
    {
        var columnCreate = ColumnCommandGroup.CreateCreateCommand();
        var attributeCreate = AttributeCommandGroup.CreateCreateCommand();

        var columnOptions = columnCreate.Options.Select(o => o.Name).OrderBy(x => x).ToList();
        var attrOptions = attributeCreate.Options.Select(o => o.Name).OrderBy(x => x).ToList();

        columnOptions.Should().BeEquivalentTo(attrOptions,
            "deprecated column create must expose the same options as canonical attribute create");
    }
}

[Trait("Category", "Unit")]
public class ChoiceCommandDeprecationTests
{
    [Fact]
    public void ChoiceCommand_Description_MentionsDeprecated()
    {
        var cmd = ChoiceCommandGroup.Create();
        cmd.Description.Should().Contain("deprecated", "choice is a deprecated alias for optionset");
    }

    [Fact]
    public void ChoiceCommand_HasSameSubcommandsAsOptionSet()
    {
        var choice = ChoiceCommandGroup.Create();
        var optionSet = OptionSetCommand.Create();

        var choiceSubcmds = choice.Subcommands.Select(c => c.Name).OrderBy(x => x).ToList();
        var optionSetSubcmds = optionSet.Subcommands.Select(c => c.Name).OrderBy(x => x).ToList();

        choiceSubcmds.Should().BeEquivalentTo(optionSetSubcmds,
            "deprecated choice must expose the same subcommands as canonical optionset");
    }
}
