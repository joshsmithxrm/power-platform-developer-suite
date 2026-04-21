using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that creates a new global choice (option set) in Dataverse.
/// </summary>
[McpServerToolType]
public sealed class MetadataCreateChoiceTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCreateChoiceTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataCreateChoiceTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Creates a new global choice (option set) in the specified solution.
    /// </summary>
    /// <param name="solution">The unique name of the solution to add the choice to.</param>
    /// <param name="schemaName">The schema name for the global choice.</param>
    /// <param name="displayName">The display name of the global choice.</param>
    /// <param name="description">A description of the global choice.</param>
    /// <param name="optionsJson">JSON array of option definitions: [{"label": "Active", "value": 1}, {"label": "Inactive", "value": 2}].</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the name and metadata ID of the created choice.</returns>
    [McpServerTool(Name = "ppds_metadata_create_choice")]
    [Description("Creates a new global choice (option set) in the specified solution. Options are provided as a JSON array of {label, value} objects. Supports dry-run mode.")]
    public async Task<MetadataCreateChoiceResult> ExecuteAsync(
        [Description("Unique name of the solution to add the choice to.")] string solution,
        [Description("Schema name for the global choice.")] string schemaName,
        [Description("Display name of the global choice.")] string displayName,
        [Description("Description of the global choice.")] string description,
        [Description("JSON array of option definitions: [{\"label\": \"Active\", \"value\": 1}, {\"label\": \"Inactive\", \"value\": 2}].")] string optionsJson,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Context.IsReadOnly)
                throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

            await using var serviceProvider = await CreateScopeAsync(cancellationToken,
                (nameof(solution), solution),
                (nameof(schemaName), schemaName),
                (nameof(displayName), displayName),
                (nameof(description), description),
                (nameof(optionsJson), optionsJson)).ConfigureAwait(false);

            OptionDefinition[] options;
            try
            {
                options = JsonSerializer.Deserialize<OptionDefinition[]>(optionsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? [];
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"Invalid optionsJson: {ex.Message}. Expected JSON array of {{\"label\": \"...\", \"value\": N}}.",
                    nameof(optionsJson), ex);
            }

            var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            var result = await service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
            {
                SolutionUniqueName = solution,
                SchemaName = schemaName,
                DisplayName = displayName,
                Description = description,
                Options = options,
                DryRun = dryRun
            }, ct: cancellationToken).ConfigureAwait(false);

            return new MetadataCreateChoiceResult
            {
                Name = result.Name,
                MetadataId = result.MetadataId == Guid.Empty ? null : result.MetadataId.ToString(),
                WasDryRun = result.WasDryRun
            };
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }
}

/// <summary>
/// Result of the metadata_create_choice tool.
/// </summary>
public sealed class MetadataCreateChoiceResult
{
    /// <summary>The name of the created global choice.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The metadata identifier of the created global choice.</summary>
    [JsonPropertyName("metadataId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataId { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
