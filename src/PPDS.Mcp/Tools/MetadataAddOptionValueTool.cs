using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that adds a new option value to an existing option set.
/// </summary>
[McpServerToolType]
public sealed class MetadataAddOptionValueTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataAddOptionValueTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataAddOptionValueTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Adds a new option value to an existing option set.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the option set.</param>
    /// <param name="optionSetName">The name of the option set.</param>
    /// <param name="label">The label for the new option.</param>
    /// <param name="value">The numeric value for the new option. Auto-assigned if not provided.</param>
    /// <param name="description">Description for the new option.</param>
    /// <param name="color">Color associated with the option (hex string, e.g., '#FF0000').</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the assigned integer value.</returns>
    [McpServerTool(Name = "ppds_metadata_add_option_value")]
    [Description("Adds a new option value to an existing option set (global or local choice). Returns the assigned integer value.")]
    public async Task<MetadataAddOptionValueResult> ExecuteAsync(
        [Description("Unique name of the solution containing the option set.")] string solution,
        [Description("Name of the option set to add the value to.")] string optionSetName,
        [Description("Label for the new option.")] string label,
        [Description("Numeric value for the new option. Auto-assigned if omitted (optional).")] int? value = null,
        [Description("Description for the new option (optional).")] string? description = null,
        [Description("Color associated with the option as hex string, e.g., '#FF0000' (optional).")] string? color = null,
        CancellationToken cancellationToken = default)
    {
        if (Context.IsReadOnly)
            throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

        await using var serviceProvider = await CreateScopeAsync(cancellationToken,
            (nameof(solution), solution),
            (nameof(optionSetName), optionSetName),
            (nameof(label), label)).ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        var assignedValue = await service.AddOptionValueAsync(new AddOptionValueRequest
        {
            SolutionUniqueName = solution,
            OptionSetName = optionSetName,
            Label = label,
            Value = value,
            Description = description,
            Color = color
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataAddOptionValueResult
        {
            Value = assignedValue
        };
    }
}

/// <summary>
/// Result of the metadata_add_option_value tool.
/// </summary>
public sealed class MetadataAddOptionValueResult
{
    /// <summary>The assigned integer value of the new option.</summary>
    [JsonPropertyName("value")]
    public int Value { get; set; }
}
