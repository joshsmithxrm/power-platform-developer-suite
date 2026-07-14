using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;
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
    /// Rejects <see cref="Commands.OutputFormat.Csv"/> — most commands do not implement CSV rendering (#1078).
    /// </summary>
    public static readonly Option<OutputFormat> OutputFormat = CreateOutputFormatOption();

    /// <summary>
    /// Output format variant for the commands that implement CSV rendering
    /// (query sql, query fetch, query history execute, plugin traces list).
    /// Added via <see cref="AddToCommand"/> with <c>supportsCsv: true</c>.
    /// </summary>
    public static readonly Option<OutputFormat> CsvCapableOutputFormat = CreateOutputFormatOption(supportsCsv: true);

    /// <summary>
    /// Correlation ID for distributed tracing. Auto-generated if not provided.
    /// </summary>
    public static readonly Option<string?> CorrelationId = new("--correlation-id")
    {
        Description = "Correlation ID for distributed tracing"
    };

    /// <summary>
    /// Creates an --output-format option with a clean parse error for invalid values (#1076)
    /// and, unless <paramref name="supportsCsv"/> is set, a validator rejecting CSV (#1078).
    /// Help usage label and shell completions advertise only the instance's valid set (#1336).
    /// </summary>
    /// <param name="supportsCsv">Whether the owning command implements CSV rendering.</param>
    /// <param name="shortAlias">Short alias — "-f" everywhere except data load, where -f belongs to --file.</param>
    public static Option<OutputFormat> CreateOutputFormatOption(bool supportsCsv = false, string shortAlias = "-f")
    {
        string[] validValueNames = supportsCsv
            ? [nameof(Commands.OutputFormat.Text), nameof(Commands.OutputFormat.Json), nameof(Commands.OutputFormat.Csv)]
            : [nameof(Commands.OutputFormat.Text), nameof(Commands.OutputFormat.Json)];
        var validValues = string.Join(", ", validValueNames);

        var option = new Option<OutputFormat>("--output-format", shortAlias)
        {
            Description = "Output format",
            // Without an explicit HelpName, System.CommandLine derives the usage label from the
            // enum type and advertises <Csv|Json|Text> even where the validator below rejects
            // Csv (#1336). Pin the label to this instance's actual valid set instead.
            HelpName = string.Join("|", validValueNames),
            DefaultValueFactory = _ => Commands.OutputFormat.Text,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return Commands.OutputFormat.Text; // Missing value is reported by arity enforcement.
                }

                var token = result.Tokens[^1].Value;
                if (Enum.TryParse<OutputFormat>(token, ignoreCase: true, out var format)
                    && Enum.IsDefined(format))
                {
                    return format;
                }

                result.AddError($"Invalid value '{token}' for --output-format. Valid values: {validValues}.");
                return Commands.OutputFormat.Text;
            }
        };

        // Replace the enum-derived completion source (Csv|Json|Text) so shell completions offer
        // only the values this instance accepts (#1336).
        option.CompletionSources.Clear();
        option.CompletionSources.Add(_ => validValueNames.Select(name => new CompletionItem(name)));

        if (!supportsCsv)
        {
            option.Validators.Add(result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return; // Default value (Text) — nothing to reject.
                }

                if (Enum.TryParse<OutputFormat>(result.Tokens[^1].Value, ignoreCase: true, out var format)
                    && format == Commands.OutputFormat.Csv)
                {
                    result.AddError(
                        $"CSV output is not supported for '{GetCommandPath(result)}'. Use --output-format Json or Text.");
                }
            });
        }

        return option;
    }

    /// <summary>
    /// Builds the user-facing command path (e.g. "schema compare") from a parse result,
    /// excluding the executable root.
    /// </summary>
    private static string GetCommandPath(OptionResult optionResult)
    {
        var names = new List<string>();
        SymbolResult? current = optionResult.Parent;
        while (current is CommandResult commandResult)
        {
            if (commandResult.Command is not RootCommand)
            {
                names.Add(commandResult.Command.Name);
            }

            current = commandResult.Parent;
        }

        if (names.Count == 0)
        {
            return "this command";
        }

        names.Reverse();
        return string.Join(" ", names);
    }

    /// <summary>
    /// Adds the global options to a command.
    /// </summary>
    /// <param name="command">The command to add options to.</param>
    /// <param name="includeOutputFormat">Whether to include --output-format (skip if command has its own).</param>
    /// <param name="supportsCsv">Whether the command implements CSV rendering for --output-format Csv.</param>
    public static void AddToCommand(Command command, bool includeOutputFormat = true, bool supportsCsv = false)
    {
        command.Options.Add(Quiet);
        command.Options.Add(Verbose);
        command.Options.Add(Debug);
        command.Options.Add(CorrelationId);

        if (includeOutputFormat)
        {
            command.Options.Add(supportsCsv ? CsvCapableOutputFormat : OutputFormat);
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
            OutputFormat = GetOutputFormatValue(parseResult),
            CorrelationId = parseResult.GetValue(CorrelationId)
        };
    }

    /// <summary>
    /// Reads the output format from whichever --output-format instance the command carries:
    /// the shared <see cref="OutputFormat"/>/<see cref="CsvCapableOutputFormat"/> statics, or a
    /// command-local instance (resolved by option name so <see cref="GetValues"/> stays correct).
    /// </summary>
    private static OutputFormat GetOutputFormatValue(System.CommandLine.ParseResult parseResult)
    {
        if (parseResult.GetResult(CsvCapableOutputFormat) is { } csvCapableResult)
        {
            return csvCapableResult.GetValueOrDefault<OutputFormat>();
        }

        if (parseResult.GetResult(OutputFormat) is { } result)
        {
            return result.GetValueOrDefault<OutputFormat>();
        }

        // Fallback for commands that declare their own local --output-format option
        // instead of the shared statics — resolve it by name so we never silently
        // fall through to Text.
        foreach (var option in parseResult.CommandResult.Command.Options)
        {
            if (option.Name == "--output-format" && parseResult.GetResult(option) is { } localResult)
            {
                return localResult.GetValueOrDefault<OutputFormat>();
            }
        }

        return Commands.OutputFormat.Text;
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
