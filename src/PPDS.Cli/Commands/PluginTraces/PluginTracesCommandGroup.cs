using System.CommandLine;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// PluginTraces command group for querying and managing plugin trace logs.
/// </summary>
public static class PluginTracesCommandGroup
{
    /// <summary>
    /// Result of parsing a --record option value.
    /// </summary>
    public sealed class RecordParseResult
    {
        /// <summary>
        /// Whether parsing succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The parsed entity name (if successful).
        /// </summary>
        public string? EntityName { get; init; }

        /// <summary>
        /// Error message (if parsing failed).
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Parses the --record option value into an entity name.
    /// Supported formats: "entity" or "entity/guid".
    /// </summary>
    /// <param name="record">The raw --record option value.</param>
    /// <returns>Parse result with entity name or error message.</returns>
    public static RecordParseResult ParseRecordOption(string? record)
    {
        if (string.IsNullOrEmpty(record))
        {
            return new RecordParseResult { Success = true, EntityName = null };
        }

        // Split and validate format
        var segments = record.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return new RecordParseResult
            {
                Success = false,
                ErrorMessage = "Invalid --record format. Expected 'entity' or 'entity/guid'. Example: account or account/00000000-0000-0000-0000-000000000000."
            };
        }

        if (segments.Length > 2)
        {
            return new RecordParseResult
            {
                Success = false,
                ErrorMessage = "Invalid --record format. Expected 'entity' or 'entity/guid'. Too many segments."
            };
        }

        // If a GUID segment is provided, validate it
        if (segments.Length == 2 && !Guid.TryParse(segments[1], out _))
        {
            return new RecordParseResult
            {
                Success = false,
                ErrorMessage = "Invalid --record format. The identifier after the slash must be a valid GUID."
            };
        }

        return new RecordParseResult
        {
            Success = true,
            EntityName = segments[0]
        };
    }

    /// <summary>
    /// Profile option for authentication.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for target environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'plugintraces' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("plugintraces", "Query and manage plugin trace logs: list, get, related, timeline, settings, delete");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(SettingsCommand.Create());
        command.Subcommands.Add(RelatedCommand.Create());
        command.Subcommands.Add(TimelineCommand.Create());
        command.Subcommands.Add(DeleteCommand.Create());

        return command;
    }
}
