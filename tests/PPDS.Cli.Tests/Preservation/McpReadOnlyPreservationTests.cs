namespace PPDS.Cli.Tests.Preservation;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

public class McpReadOnlyPreservationTests
{
    [Theory]
    [InlineData("WebResourcesPublishTool.cs")]
    [InlineData("QuerySqlTool.cs")]
    [InlineData("MetadataUpdateChoiceTool.cs")]
    [InlineData("MetadataUpdateColumnTool.cs")]
    [InlineData("MetadataUpdateRelationshipTool.cs")]
    [InlineData("MetadataUpdateTableTool.cs")]
    [InlineData("PluginTracesDeleteTool.cs")]
    [InlineData("MetadataCreateChoiceTool.cs")]
    [InlineData("MetadataCreateKeyTool.cs")]
    [InlineData("MetadataCreateRelationshipTool.cs")]
    [InlineData("MetadataCreateTableTool.cs")]
    [InlineData("EnvironmentVariablesSetTool.cs")]
    [InlineData("MetadataAddColumnTool.cs")]
    [InlineData("MetadataAddOptionValueTool.cs")]
    public void EnumeratedTools_StillCheckIsReadOnly(string toolFileName)
    {
        var toolsRoot = Path.Combine(PathHelpers.RepoRoot(), "src", "PPDS.Mcp", "Tools");
        var matches = Directory.GetFiles(toolsRoot, toolFileName, SearchOption.AllDirectories);
        Assert.True(matches.Length == 1, $"Expected exactly 1 match for {toolFileName}, got {matches.Length}.");
        var filePath = matches[0];
        var lines = File.ReadAllLines(filePath);

        var found = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\bContext\.IsReadOnly\b"))
            {
                // search the next 5 lines for `throw` or `return`
                var end = Math.Min(lines.Length, i + 6);
                for (int j = i; j < end; j++)
                {
                    if (Regex.IsMatch(lines[j], @"\b(throw|return)\b"))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
        }

        Assert.True(found, $"{toolFileName}: no `Context.IsReadOnly` followed by throw/return within 5 lines.");
    }
}
