using System.ComponentModel;
using ModelContextProtocol.Server;
using PPDS.Mcp;

namespace PPDS.DocsGen.Mcp.Tests.Fixtures;

/// <summary>
/// Fixture tool exercising the canonical PPDS pattern: [McpServerTool(Name=...)]
/// + sibling [System.ComponentModel.Description] on the ExecuteAsync method.
/// Also carries two [McpToolExample] attributes — verifies AC-17 example rendering
/// and the multiple-attribute path.
/// </summary>
[McpServerToolType]
public sealed class AlphaEchoTool
{
    [McpServerTool(Name = "fixture_alpha_echo")]
    [Description("Echoes the given message back to the caller.")]
    [McpToolExample("{\"message\":\"hello\"}", "{\"echoed\":\"hello\"}")]
    [McpToolExample("{\"message\":\"world\"}")]
    public string ExecuteAsync(
        [Description("The message to echo.")] string message,
        int repeat = 1,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return string.Concat(Enumerable.Repeat(message, repeat));
    }
}

/// <summary>
/// Fixture tool exercising the no-examples path. Verifies that the
/// ## Examples section is omitted rather than placeholder-filled. The generator
/// also prefers a <c>Description</c> named arg on [McpServerTool] when present;
/// since the real <c>McpServerToolAttribute</c> shipping in MCP 0.2.0-preview.3
/// does not expose such a property, the sibling <see cref="DescriptionAttribute"/>
/// pattern is the sole path used by real PPDS code — exercised here too.
/// </summary>
[McpServerToolType]
public sealed class EnvListFixtureTool
{
    [McpServerTool(Name = "fixture_env_list")]
    [Description("Lists fixture environments.")]
    public int ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return 42;
    }
}

/// <summary>
/// Fixture tool exercising the "missing Description" diagnostic path. Only Name
/// is supplied and no sibling [Description] attribute — the generator should emit
/// the tool anyway (with an empty description) and add a diagnostic line.
/// Present to verify the generator does not crash when PPDS016 is bypassed.
/// </summary>
[McpServerToolType]
public sealed class MetadataNoDescFixtureTool
{
    [McpServerTool(Name = "fixture_metadata_nodesc")]
    public void ExecuteAsync() { }
}
