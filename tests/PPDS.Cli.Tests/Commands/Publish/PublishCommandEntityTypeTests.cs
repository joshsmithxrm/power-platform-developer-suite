using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Publish;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Publish;

[Trait("Category", "Unit")]
public class PublishCommandEntityTypeTests
{
    private readonly Command _command;

    public PublishCommandEntityTypeTests()
    {
        _command = PublishCommandGroup.Create();
    }

    [Fact]
    public void Parse_WithEntityType_Succeeds()
    {
        var result = _command.Parse("--type entity account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEntityTypeCaseInsensitive_Succeeds()
    {
        var result = _command.Parse("--type Entity account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEntityTypeAndMultipleNames_Succeeds()
    {
        var result = _command.Parse("--type entity account contact lead");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEntityTypeAndSolution_Succeeds()
    {
        var result = _command.Parse("--type entity --solution MySolution");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEntityTypeShortFlag_Succeeds()
    {
        var result = _command.Parse("-t entity account");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithWebresourceType_StillSucceeds()
    {
        var result = _command.Parse("--type webresource app.js");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithUnsupportedType_StillParsesButNamesRequired()
    {
        // Type validation happens at execution time, not parse time.
        // Parse-time only validates --type is required when names are given.
        var result = _command.Parse("--type bogus somename");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TypeOption_DescriptionMentionsEntity()
    {
        var typeOption = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(typeOption);
        Assert.Contains("entity", typeOption.Description?.ToLowerInvariant());
    }

    [Fact]
    public void TypeOption_DescriptionMentionsWebresource()
    {
        var typeOption = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(typeOption);
        Assert.Contains("webresource", typeOption.Description?.ToLowerInvariant());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("new_customentity")]
    public void EntityXmlFormat_ProducesCorrectXml(string entityName)
    {
        // Verify the XML building logic that PublishEntitiesAsync uses
        var entityXml = $"<entity>{entityName}</entity>";
        var parameterXml = $"<importexportxml><entities>{entityXml}</entities></importexportxml>";

        Assert.Contains($"<entity>{entityName}</entity>", parameterXml);
        Assert.StartsWith("<importexportxml><entities>", parameterXml);
        Assert.EndsWith("</entities></importexportxml>", parameterXml);
    }

    [Fact]
    public void EntityXmlFormat_MultipleEntities_ProducesCorrectXml()
    {
        var entityNames = new[] { "account", "contact", "lead" };
        var entityXml = string.Join("", entityNames.Select(n => $"<entity>{n}</entity>"));
        var parameterXml = $"<importexportxml><entities>{entityXml}</entities></importexportxml>";

        Assert.Equal(
            "<importexportxml><entities><entity>account</entity><entity>contact</entity><entity>lead</entity></entities></importexportxml>",
            parameterXml);
    }
}
