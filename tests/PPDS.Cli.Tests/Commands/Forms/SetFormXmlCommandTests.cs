using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using PPDS.Cli.Commands.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

/// <summary>
/// Tests for the <c>set-xml</c> command structure (AC-07, AC-30).
/// </summary>
public class SetFormXmlCommandTests
{
    private readonly Command _command;

    public SetFormXmlCommandTests()
    {
        _command = SetXmlCommand.Create();
    }

    // ── AC-07: Command identity ───────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasCorrectName()
    {
        _command.Name.Should().Be("set-xml");
    }

    // ── AC-07: Required options present ──────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");

        option.Should().NotBeNull(because: "--entity is required for set-xml");
        option!.Required.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasFormOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--form");

        option.Should().NotBeNull(because: "--form is required for set-xml");
        option!.Required.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasXmlOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--xml");

        option.Should().NotBeNull(because: "--xml is required for set-xml");
        option!.Required.Should().BeTrue();
    }

    // ── AC-30: Validation help text ───────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HelpTextDescribesSchemaValidation()
    {
        // AC-30: The command description must communicate that schema validation
        // is applied and that GUIDs must use brace format and be unique.
        var description = _command.Description ?? string.Empty;

        description.Should().ContainAny(
            new[] { "schema", "Schema", "validation", "Validation" },
            because: "the description must mention schema validation (AC-30)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HelpTextMentionsBraceFormatGuids()
    {
        var description = _command.Description ?? string.Empty;

        description.Should().ContainAny(
            new[] { "brace", "GUID", "guid", "{", "}" },
            because: "the description must document brace-format GUID requirement (AC-30)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HelpTextMentionsUniqueness()
    {
        var description = _command.Description ?? string.Empty;

        description.Should().ContainAny(
            new[] { "unique", "Unique" },
            because: "the description must document that id/labelid values must be unique (AC-30)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void XmlOption_HelpTextDescribesValidation()
    {
        // The --xml option description also documents validation requirements
        var xmlOption = _command.Options.FirstOrDefault(o => o.Name == "--xml");
        xmlOption.Should().NotBeNull();

        var desc = xmlOption!.Description ?? string.Empty;
        desc.Should().ContainAny(
            new[] { "validation", "Validation", "schema", "Schema", "brace", "GUID", "guid" },
            because: "the --xml option description must document validation (AC-30)");
    }

    // ── Parse happy path / error paths ────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithAllRequired_Succeeds()
    {
        var result = _command.Parse("--entity account --form \"Main Form\" --xml path.xml");

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutXml_HasErrors()
    {
        var result = _command.Parse("--entity account --form \"Main Form\"");

        result.Errors.Should().NotBeEmpty(because: "--xml is required");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutEntity_HasErrors()
    {
        var result = _command.Parse("--form \"Main Form\" --xml path.xml");

        result.Errors.Should().NotBeEmpty(because: "--entity is required");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_WithoutForm_HasErrors()
    {
        var result = _command.Parse("--entity account --xml path.xml");

        result.Errors.Should().NotBeEmpty(because: "--form is required");
    }

    // ── Global options present ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");

        option.Should().NotBeNull(because: "--output-format (AC-34) must be present on every forms subcommand");
    }

    // ── Optional options ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasOptionalPublishOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--publish");

        option.Should().NotBeNull();
        option!.Required.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Command_HasOptionalSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");

        option.Should().NotBeNull();
        option!.Required.Should().BeFalse();
    }
}
