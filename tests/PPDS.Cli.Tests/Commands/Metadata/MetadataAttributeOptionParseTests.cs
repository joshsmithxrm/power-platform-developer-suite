using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers #1161 local Choice option parsing (AC-54) and --choice/local mutual exclusion (AC-53).
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAttributeOptionParseTests
{
    // ---- ParseOptionSpec: "Label[:Value][:#Color]" ----

    [Fact]
    public void ParseOptionSpec_LabelOnly_NoValueOrColor()
    {
        var def = AttributeCommandGroup.ParseOptionSpec("Mild");
        def.Label.Should().Be("Mild");
        def.Value.Should().Be(0);
        def.Color.Should().BeNull();
    }

    [Fact]
    public void ParseOptionSpec_LabelAndValue()
    {
        var def = AttributeCommandGroup.ParseOptionSpec("Moderate:864630001");
        def.Label.Should().Be("Moderate");
        def.Value.Should().Be(864630001);
    }

    [Fact]
    public void ParseOptionSpec_LabelValueColor()
    {
        var def = AttributeCommandGroup.ParseOptionSpec("Severe:864630002:#FF8800");
        def.Label.Should().Be("Severe");
        def.Value.Should().Be(864630002);
        def.Color.Should().Be("#FF8800");
    }

    [Fact]
    public void ParseOptionSpec_LabelAndColor_NoValue()
    {
        var def = AttributeCommandGroup.ParseOptionSpec("Critical:#FF0000");
        def.Label.Should().Be("Critical");
        def.Value.Should().Be(0);
        def.Color.Should().Be("#FF0000");
    }

    [Fact]
    public void ParseOptionSpec_EmptyLabel_Throws()
    {
        var act = () => AttributeCommandGroup.ParseOptionSpec(":5");
        act.Should().Throw<System.FormatException>();
    }

    // ---- ParseOptionSpecs: source precedence ----

    [Fact]
    public void ParseOptionSpecs_RepeatedOption_PreservesOrder()
    {
        var result = AttributeCommandGroup.ParseOptionSpecs(
            new[] { "Mild:1", "Moderate:2", "Severe:3" }, optionsFile: null, legacyCsv: null);

        result.Should().NotBeNull();
        result!.Select(o => o.Label).Should().ContainInOrder("Mild", "Moderate", "Severe");
        result.Select(o => o.Value).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public void ParseOptionSpecs_OptionsFile_ParsesJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[{\"label\":\"Mild\",\"value\":864630000,\"color\":\"#00FF00\"},{\"label\":\"Severe\"}]");
            var result = AttributeCommandGroup.ParseOptionSpecs(null, optionsFile: path, legacyCsv: null);

            result.Should().NotBeNull();
            result.Should().HaveCount(2);
            result![0].Label.Should().Be("Mild");
            result[0].Value.Should().Be(864630000);
            result[0].Color.Should().Be("#00FF00");
            result[1].Label.Should().Be("Severe");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseOptionSpecs_LegacyCsv_StillSupported()
    {
        var result = AttributeCommandGroup.ParseOptionSpecs(null, null, "Active=1,Inactive=2");
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void ParseOptionSpecs_NoneSupplied_ReturnsNull()
    {
        AttributeCommandGroup.ParseOptionSpecs(null, null, null).Should().BeNull();
    }

    // ---- create flag surface (#1161) ----

    [Fact]
    public void Create_HasChoiceOptionAndLocalOptionFlags()
    {
        var create = AttributeCommandGroup.CreateCreateCommand();
        var names = create.Options.Select(o => o.Name).ToHashSet();
        names.Should().Contain("--choice");
        names.Should().Contain("--option");
        names.Should().Contain("--options-file");
        names.Should().Contain("--publish");
    }

    // ---- AC-53: --choice mutually exclusive with local options ----

    [Fact]
    public async Task Create_ChoiceWithLocalOptions_ReturnsValidationError()
    {
        var create = AttributeCommandGroup.CreateCreateCommand();
        var parse = create.Parse(
            "--solution s --entity account --name new_sev --display-name Severity --type Choice --choice global_sev --option Mild:1");

        var exit = await parse.InvokeAsync();

        exit.Should().Be(ExitCodes.ValidationError,
            "--choice (global) and --option (local) are mutually exclusive (AC-53)");
    }
}
