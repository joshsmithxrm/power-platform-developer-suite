using System.CommandLine;
using System.Linq;
using FluentAssertions;
using PPDS.Cli.Commands.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

/// <summary>
/// Structural tests verifying stdout discipline and output-format option presence
/// across forms subcommands (AC-34).
/// </summary>
public class FormsCommandOutputTests
{
    private static Command CreateFormsGroup() => FormsCommandGroup.Create();

    // ── AC-34: --output-format present on all subcommands ────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void AllFormsSubcommands_HaveOutputFormatOption()
    {
        // AC-34: All subcommands must support --output-format so that machine consumers
        // can receive structured JSON output via stdout while status messages go to stderr.
        var formsCommand = CreateFormsGroup();

        foreach (var subcommand in formsCommand.Subcommands)
        {
            var formatOption = subcommand.Options.FirstOrDefault(o => o.Name == "--output-format");
            formatOption.Should().NotBeNull(
                because: $"subcommand '{subcommand.Name}' must expose --output-format (AC-34 stdout discipline)");
        }
    }

    // ── Command structural sanity ─────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void ListCommand_Create_HasDescription()
    {
        var command = ListCommand.Create();

        command.Description.Should().NotBeNullOrWhiteSpace(
            because: "list subcommand must have a description");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCommand_Create_HasDescription()
    {
        var command = GetCommand.Create();

        command.Description.Should().NotBeNullOrWhiteSpace(
            because: "get subcommand must have a description");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ListCommand_Create_HasOutputFormatOption()
    {
        var command = ListCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "list must support --output-format so data goes to stdout as JSON (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCommand_Create_HasOutputFormatOption()
    {
        var command = GetCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "get must support --output-format so data goes to stdout as JSON (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SetXmlCommand_Create_HasOutputFormatOption()
    {
        var command = SetXmlCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "set-xml must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddTabCommand_Create_HasOutputFormatOption()
    {
        var command = AddTabCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "add-tab must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateTabCommand_Create_HasOutputFormatOption()
    {
        var command = UpdateTabCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "update-tab must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddSectionCommand_Create_HasOutputFormatOption()
    {
        var command = AddSectionCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "add-section must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindTabCommand_Create_HasOutputFormatOption()
    {
        var command = FindTabCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "find-tab must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindSectionCommand_Create_HasOutputFormatOption()
    {
        var command = FindSectionCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "find-section must support --output-format (AC-34)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddSubgridCommand_Create_HasOutputFormatOption()
    {
        var command = AddSubgridCommand.Create();

        command.Options.Should().Contain(
            o => o.Name == "--output-format",
            because: "add-subgrid must support --output-format (AC-34)");
    }
}
