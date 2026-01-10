// Pattern: CLI Command with Spectre.Console
// Demonstrates: Output routing, dual modes, exception mapping, exit codes
// Related: ADR-0008, ADR-0009, CLAUDE.md "Write CLI status to stderr"
// Source: src/PPDS.Cli/Commands/Connections/ListCommand.cs

// KEY PRINCIPLES:
// 1. Data to stdout, status/errors to stderr
// 2. Support both JSON and human-readable output
// 3. Map exceptions to structured errors + exit codes
// 4. Use ServiceFactory for consistent DI setup

using System.CommandLine;
using Spectre.Console;
using PPDS.Cli.Infrastructure;

public static class ExportCommand
{
    public static Command Create()
    {
        // PATTERN: Define options with descriptions
        var entityOption = new Option<string>("--entity", "Entity logical name")
        {
            IsRequired = true
        };

        var outputOption = new Option<string>("--output", "Output file path")
        {
            IsRequired = true
        };

        var formatOption = new Option<ExportFormat>("--format", () => ExportFormat.Json)
        {
            Description = "Output format (json, csv, xml)"
        };

        var command = new Command("export", "Export entity data to file")
        {
            entityOption,
            outputOption,
            formatOption
        };

        // PATTERN: Add shared options (profile, environment, verbose, etc.)
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var format = parseResult.GetValue(formatOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, output, format, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string outputPath,
        ExportFormat format,
        GlobalOptions options,
        CancellationToken cancellationToken)
    {
        // PATTERN: Create output writer based on --json flag
        var writer = ServiceFactory.CreateOutputWriter(options);

        try
        {
            // PATTERN: Create service provider with profile/environment context
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                options.Profile,
                options.Environment,
                options.Verbose,
                options.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            // PATTERN: Status messages to stderr (not stdout)
            if (!options.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();  // Blank line separator
            }

            var exportService = serviceProvider.GetRequiredService<IDataExportService>();

            // PATTERN: Progress reporter for long operations
            var progress = options.IsJsonMode
                ? null  // No progress in JSON mode
                : new ConsoleProgressReporter();

            var result = await exportService.ExportAsync(
                new ExportOptions
                {
                    EntityName = entity,
                    OutputPath = outputPath,
                    Format = format
                },
                progress,
                cancellationToken);

            // PATTERN: Output based on mode
            if (options.IsJsonMode)
            {
                // JSON: Data to stdout
                writer.WriteSuccess(new ExportOutput
                {
                    RecordCount = result.RecordCount,
                    OutputPath = result.OutputPath
                });
            }
            else
            {
                // Human: Formatted output to stdout
                Console.WriteLine();
                Console.WriteLine($"Exported {result.RecordCount:N0} records to {result.OutputPath}");
            }

            return ExitCodes.Success;
        }
        catch (PpdsValidationException ex)
        {
            // PATTERN: Validation errors get specific handling
            writer.WriteError(new StructuredError(ex.ErrorCode, ex.UserMessage)
            {
                Errors = ex.Errors.Select(e => new ErrorDetail(e.Field, e.Message)).ToList()
            });
            return ExitCodes.ValidationError;
        }
        catch (PpdsAuthException ex)
        {
            // PATTERN: Auth errors suggest re-authentication
            writer.WriteError(new StructuredError(ex.ErrorCode, ex.UserMessage));

            if (!options.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Run 'ppds auth create' to re-authenticate.");
            }

            return ExitCodes.AuthError;
        }
        catch (PpdsNotFoundException ex)
        {
            // PATTERN: Not found with context
            writer.WriteError(new StructuredError(
                ex.ErrorCode,
                ex.UserMessage,
                context: $"{ex.ResourceType}: {ex.ResourceId}"));
            return ExitCodes.NotFoundError;
        }
        catch (Exception ex)
        {
            // PATTERN: Catch-all with exception mapping
            var error = ExceptionMapper.Map(ex,
                context: $"exporting {entity}",
                debug: options.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}

// OUTPUT WRITER: Handles JSON vs human-readable consistently
public class OutputWriter
{
    private readonly bool _isJsonMode;
    private readonly bool _isDebug;

    public void WriteSuccess<T>(T data)
    {
        if (_isJsonMode)
        {
            // JSON output to stdout
            var json = JsonSerializer.Serialize(data, JsonOptions.Default);
            Console.WriteLine(json);
        }
    }

    public void WriteError(StructuredError error)
    {
        if (_isJsonMode)
        {
            // JSON error to stderr
            var json = JsonSerializer.Serialize(new { error }, JsonOptions.Default);
            Console.Error.WriteLine(json);
        }
        else
        {
            // Human error to stderr
            Console.Error.WriteLine($"Error: {error.Message}");

            if (_isDebug && error.StackTrace != null)
            {
                Console.Error.WriteLine(error.StackTrace);
            }
        }
    }
}

// EXIT CODES: Consistent across all commands
public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int ValidationError = 2;
    public const int AuthError = 3;
    public const int NotFoundError = 4;
    public const int ThrottleError = 5;
    public const int PermissionError = 6;
}

// EXCEPTION MAPPER: Converts exceptions to structured errors + exit codes
public static class ExceptionMapper
{
    public static StructuredError Map(Exception ex, string context, bool debug)
    {
        return new StructuredError(
            GetErrorCode(ex),
            GetUserMessage(ex, context),
            stackTrace: debug ? ex.StackTrace : null);
    }

    public static int ToExitCode(Exception ex) => ex switch
    {
        PpdsValidationException => ExitCodes.ValidationError,
        PpdsAuthException => ExitCodes.AuthError,
        PpdsNotFoundException => ExitCodes.NotFoundError,
        _ => ExitCodes.GeneralError
    };
}

// ANTI-PATTERNS TO AVOID:
//
// BAD: Data to stderr
// Console.Error.WriteLine($"Found {count} records");  // WRONG for data
//
// BAD: Status to stdout (breaks JSON piping)
// Console.WriteLine("Connecting...");  // WRONG - pollutes data stream
//
// BAD: No exit codes
// catch (Exception) { return 0; }  // WRONG - caller can't detect failure
//
// BAD: Raw exception message to user
// catch (Exception ex) { Console.Error.WriteLine(ex.Message); }  // WRONG - may expose internals
