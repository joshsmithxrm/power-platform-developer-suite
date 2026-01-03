using System.CommandLine;
using PPDS.Cli.Commands;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Global CLI options shared across all commands.
/// </summary>
/// <remarks>
/// <para>
/// These options control logging verbosity, output format, and distributed tracing.
/// They are added to commands that need them via <see cref="AddToCommand"/>.
/// </para>
/// <para>
/// Note: System.CommandLine doesn't support true global options that automatically
/// apply to all subcommands. Each command must explicitly add and handle these options.
/// </para>
/// </remarks>
public static class GlobalOptions
{
    /// <summary>
    /// Show only warnings and errors. Mutually exclusive with --verbose and --debug.
    /// </summary>
    public static readonly Option<bool> Quiet = new("--quiet", "-q")
    {
        Description = "Show only warnings and errors"
    };

    /// <summary>
    /// Show debug-level messages. Mutually exclusive with --quiet and --debug.
    /// </summary>
    public static readonly Option<bool> Verbose = new("--verbose", "-v")
    {
        Description = "Show detailed output including debug messages"
    };

    /// <summary>
    /// Show trace-level diagnostic output. Mutually exclusive with --quiet and --verbose.
    /// </summary>
    public static readonly Option<bool> Debug = new("--debug")
    {
        Description = "Show trace-level diagnostic output"
    };

    /// <summary>
    /// Output format: text (human-readable) or json (machine-readable).
    /// </summary>
    public static readonly Option<OutputFormat> OutputFormat = new("--output-format", "-f")
    {
        Description = "Output format",
        DefaultValueFactory = _ => Commands.OutputFormat.Text
    };

    /// <summary>
    /// Correlation ID for distributed tracing. Auto-generated if not provided.
    /// </summary>
    public static readonly Option<string?> CorrelationId = new("--correlation-id")
    {
        Description = "Correlation ID for distributed tracing"
    };

    /// <summary>
    /// Adds the global options to a command.
    /// </summary>
    /// <param name="command">The command to add options to.</param>
    /// <param name="includeOutputFormat">Whether to include --output-format (skip if command has its own).</param>
    public static void AddToCommand(Command command, bool includeOutputFormat = true)
    {
        command.Options.Add(Quiet);
        command.Options.Add(Verbose);
        command.Options.Add(Debug);
        command.Options.Add(CorrelationId);

        if (includeOutputFormat)
        {
            command.Options.Add(OutputFormat);
        }

        // Add validator for mutually exclusive verbosity options
        command.Validators.Add(result =>
        {
            var quiet = result.GetValue(Quiet);
            var verbose = result.GetValue(Verbose);
            var debug = result.GetValue(Debug);

            var count = (quiet ? 1 : 0) + (verbose ? 1 : 0) + (debug ? 1 : 0);
            if (count > 1)
            {
                result.AddError("Options --quiet, --verbose, and --debug are mutually exclusive.");
            }
        });
    }

    /// <summary>
    /// Gets the global option values from a parse result.
    /// </summary>
    /// <param name="parseResult">The parse result.</param>
    /// <returns>The parsed global option values.</returns>
    public static GlobalOptionValues GetValues(System.CommandLine.ParseResult parseResult)
    {
        return new GlobalOptionValues
        {
            Quiet = parseResult.GetValue(Quiet),
            Verbose = parseResult.GetValue(Verbose),
            Debug = parseResult.GetValue(Debug),
            OutputFormat = parseResult.GetValue(OutputFormat),
            CorrelationId = parseResult.GetValue(CorrelationId)
        };
    }
}

/// <summary>
/// Parsed values from global CLI options.
/// </summary>
public sealed class GlobalOptionValues
{
    /// <summary>
    /// Whether --quiet was specified.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// Whether --verbose was specified.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Whether --debug was specified.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// The output format.
    /// </summary>
    public OutputFormat OutputFormat { get; init; }

    /// <summary>
    /// The correlation ID for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Whether JSON output mode is enabled.
    /// </summary>
    public bool IsJsonMode => OutputFormat == Commands.OutputFormat.Json;
}
