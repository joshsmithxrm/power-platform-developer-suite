using System;

namespace PPDS.Mcp;

/// <summary>
/// Declares an example invocation of an MCP tool, used by documentation generators
/// to emit example sections. Multiple examples may be attached to a single tool method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class McpToolExampleAttribute : Attribute
{
    /// <summary>Example JSON input that would be passed to the tool.</summary>
    public string Input { get; }

    /// <summary>Optional expected output; if null, the example is input-only.</summary>
    public string? ExpectedOutput { get; }

    /// <summary>Creates a new example with the given input and optional expected output.</summary>
    /// <param name="input">Example JSON input passed to the tool.</param>
    /// <param name="expectedOutput">Optional expected JSON output; omit for input-only examples.</param>
    public McpToolExampleAttribute(string input, string? expectedOutput = null)
    {
        Input = input;
        ExpectedOutput = expectedOutput;
    }
}
