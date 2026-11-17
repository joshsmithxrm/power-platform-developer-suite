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
    public static readonly Option<bool> Quiet = CreateQuietOption();

    /// <summary>
    /// Show debug-level messages. Mutually exclusive with --quiet and --debug.
    /// </summary>
    public static readonly Option<bool> Verbose = CreateVerboseOption();

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

    private static Option<bool> CreateQuietOption(bool withShortAlias = true) =>
        withShortAlias
            ? new Option<bool>("--quiet", "-q") { Description = "Show only warnings and errors" }
            : new Option<bool>("--quiet") { Description = "Show only warnings and errors" };

    private static Option<bool> CreateVerboseOption(bool withShortAlias = true) =>
        withShortAlias
            ? new Option<bool>("--verbose", "-v") { Description = "Show detailed output including debug messages" }
            : new Option<bool>("--verbose") { Description = "Show detailed output including debug messages" };

    /// <summary>
    /// Creates an --output-format option with a clean parse error for invalid values (#1076)
    /// and, unless <paramref name="supportsCsv"/> is set, a validator rejecting CSV (#1078).
    /// Help usage label and shell completions advertise only the instance's valid set (#1336).
    /// </summary>
    /// <param name="supportsCsv">Whether the owning command implements CSV rendering.</param>
    /// <param name="shortAlias">Short alias — "-f" everywhere except data load, where -f belongs to --file.
    /// <c>null</c> registers the long name only (used when a command-specific option owns the alias).</param>
    public static Option<OutputFormat> CreateOutputFormatOption(bool supportsCsv = false, string? shortAlias = "-f")
    {
        string[] validValueNames = supportsCsv
            ? [nameof(Commands.OutputFormat.Text), nameof(Commands.OutputFormat.Json), nameof(Commands.OutputFormat.Csv)]
            : [nameof(Commands.OutputFormat.Text), nameof(Commands.OutputFormat.Json)];
        var validValues = string.Join(", ", validValueNames);

        var option = shortAlias is null
            ? new Option<OutputFormat>("--output-format") { Description = "Output format" }
            : new Option<OutputFormat>("--output-format", shortAlias) { Description = "Output format" };
        // Without an explicit HelpName, System.CommandLine derives the usage label from the
        // enum type and advertises <Csv|Json|Text> even where the validator below rejects
        // Csv (#1336). Pin the label to this instance's actual valid set instead.
        option.HelpName = string.Join("|", validValueNames);
        option.DefaultValueFactory = _ => Commands.OutputFormat.Text;
        option.CustomParser = result =>
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
            // Resolve through the command's own instances: ShortAliasShadowPolicy may have swapped a
            // shared option for an alias-less per-command twin.
            var quiet = result.GetValue(Resolve(result.Command, Quiet));
            var verbose = result.GetValue(Resolve(result.Command, Verbose));
            var debug = result.GetValue(Resolve(result.Command, Debug));

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
            Quiet = parseResult.GetValue(Resolve(parseResult.CommandResult.Command, Quiet)),
            Verbose = parseResult.GetValue(Resolve(parseResult.CommandResult.Command, Verbose)),
            Debug = parseResult.GetValue(Resolve(parseResult.CommandResult.Command, Debug)),
            OutputFormat = GetOutputFormatValue(parseResult),
            CorrelationId = parseResult.GetValue(Resolve(parseResult.CommandResult.Command, CorrelationId))
        };
    }

    /// <summary>
    /// Returns the instance of a shared option that <paramref name="command"/> actually carries:
    /// the shared static itself, or the alias-less per-command twin installed by
    /// <see cref="ShortAliasShadowPolicy"/> (matched by long name).
    /// </summary>
    private static Option<T> Resolve<T>(Command command, Option<T> shared)
    {
        foreach (var option in command.Options)
        {
            if (ReferenceEquals(option, shared))
            {
                return shared;
            }
        }

        foreach (var option in command.Options)
        {
            if (option is Option<T> twin && option.Name == shared.Name)
            {
                return twin;
            }
        }

        return shared;
    }

    /// <summary>
    /// Builds a fresh twin of a shared option without the given short aliases, or <c>null</c> if
    /// <paramref name="shared"/> cannot be rebuilt.
    /// </summary>
    internal static Option? CreateTwinWithoutAliases(Option shared, ISet<string> droppedAliases)
    {
        var kept = shared.Aliases.FirstOrDefault(a => !droppedAliases.Contains(a));
        if (ReferenceEquals(shared, Quiet)) return CreateQuietOption(kept is not null);
        if (ReferenceEquals(shared, Verbose)) return CreateVerboseOption(kept is not null);
        if (ReferenceEquals(shared, OutputFormat)) return CreateOutputFormatOption(false, kept);
        if (ReferenceEquals(shared, CsvCapableOutputFormat)) return CreateOutputFormatOption(true, kept);
        return null;
    }

    /// <summary>True if <paramref name="option"/> is one of the shared static instances.</summary>
    internal static bool IsShared(Option option) =>
        ReferenceEquals(option, Quiet) || ReferenceEquals(option, Verbose) || ReferenceEquals(option, Debug)
        || ReferenceEquals(option, OutputFormat) || ReferenceEquals(option, CsvCapableOutputFormat)
        || ReferenceEquals(option, CorrelationId);

    /// <summary>
    /// Reads the output format from whichever --output-format instance the command carries:
    /// the shared <see cref="OutputFormat"/>/<see cref="CsvCapableOutputFormat"/> statics, or a
    /// command-local instance (resolved by option name so <see cref="GetValues"/> stays correct).
    /// </summary>
    private static OutputFormat GetOutputFormatValue(System.CommandLine.ParseResult parseResult)
    {
        // Leaf-first: the selected command's own --output-format (shared static, per-command twin made by
        // ShortAliasShadowPolicy, or a command-local instance) wins over anything an ancestor carries.
        foreach (var option in parseResult.CommandResult.Command.Options)
        {
            if (option.Name == "--output-format" && parseResult.GetResult(option) is { } localResult)
            {
                return localResult.GetValueOrDefault<OutputFormat>();
            }
        }

        // Then the shared statics parse-wide (e.g. registered on an ancestor).
        if (parseResult.GetResult(CsvCapableOutputFormat) is { } csvCapableResult)
        {
            return csvCapableResult.GetValueOrDefault<OutputFormat>();
        }

        if (parseResult.GetResult(OutputFormat) is { } result)
        {
            return result.GetValueOrDefault<OutputFormat>();
        }

        return Commands.OutputFormat.Text;
    }

    /// <summary>
    /// Returns the effective <c>--output-format</c> for a command result, twin-aware: reads whichever
    /// <c>--output-format</c> instance the command carries (shared static, per-command twin, or local).
    /// Throws <see cref="InvalidOperationException"/> if that option failed to parse, like
    /// <c>GetValue</c>. Use instead of <c>GetValue(GlobalOptions.OutputFormat)</c>, which silently reads the
    /// default when the command carries a twin.
    /// </summary>
    public static OutputFormat GetOutputFormat(CommandResult commandResult)
    {
        foreach (var option in commandResult.Command.Options)
        {
            if (option is Option<OutputFormat> typed && option.Name == "--output-format")
            {
                return commandResult.GetValue(typed);
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
