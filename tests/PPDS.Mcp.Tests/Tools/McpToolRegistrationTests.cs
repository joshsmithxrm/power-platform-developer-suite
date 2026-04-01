using FluentAssertions;
using ModelContextProtocol.Server;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Reflection-based registration tests for MCP tools.
/// Verifies invariants across all registered tool types.
/// </summary>
[Trait("Category", "Unit")]
public sealed class McpToolRegistrationTests
{
    /// <summary>
    /// AC-22: No metadata MCP tool name may contain "delete" — metadata authoring tools
    /// are non-destructive (create and update only).
    /// </summary>
    [Fact]
    public void NoMetadataDeleteToolsRegistered()
    {
        var toolTypes = typeof(MetadataEntitiesListTool).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(McpServerToolTypeAttribute), false).Length > 0);

        foreach (var toolType in toolTypes)
        {
            var methods = toolType.GetMethods()
                .SelectMany(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false))
                .Cast<McpServerToolAttribute>();

            foreach (var attr in methods)
            {
                var name = attr.Name ?? "";
                if (!name.StartsWith("ppds_metadata_", StringComparison.OrdinalIgnoreCase))
                    continue;

                name.ToLowerInvariant().Should().NotContain("delete",
                    because: $"Metadata MCP tools must not expose delete operations (found: {name})");
            }
        }
    }

    /// <summary>
    /// Verifies all metadata authoring tools are registered with expected names.
    /// </summary>
    [Fact]
    public void MetadataAuthoringToolsAreRegistered()
    {
        var toolNames = typeof(MetadataEntitiesListTool).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(McpServerToolTypeAttribute), false).Length > 0)
            .SelectMany(t => t.GetMethods())
            .SelectMany(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false))
            .Cast<McpServerToolAttribute>()
            .Select(a => a.Name)
            .ToList();

        toolNames.Should().Contain("ppds_metadata_create_table");
        toolNames.Should().Contain("ppds_metadata_update_table");
        toolNames.Should().Contain("ppds_metadata_add_column");
        toolNames.Should().Contain("ppds_metadata_update_column");
        toolNames.Should().Contain("ppds_metadata_create_relationship");
        toolNames.Should().Contain("ppds_metadata_update_relationship");
        toolNames.Should().Contain("ppds_metadata_create_choice");
        toolNames.Should().Contain("ppds_metadata_update_choice");
        toolNames.Should().Contain("ppds_metadata_add_option_value");
        toolNames.Should().Contain("ppds_metadata_create_key");
    }
}
