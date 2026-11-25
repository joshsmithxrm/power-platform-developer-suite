using PPDS.Cli.Infrastructure.Errors;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Turns the outcome of an import (the import command, or the import half of the copy command)
/// into what the user sees and the exit code: the text lines on stderr, the progress reporter's
/// error event (the JSON line in JSON output), and the code the command returns. One place, so
/// that what is said about steps left disabled, records not accounted for and refusals is the same
/// for every command and is tested (#1934 r2 MF6).
/// </summary>
internal static class ImportOutcome
{
    /// <summary>The warning codes the CLI prints from a result: each means data the user asked for was not written.</summary>
    internal static readonly IReadOnlyList<string> PrintedWarningCodes = new[]
    {
        ImportWarningCodes.LaterPhasesSkipped,
        ImportWarningCodes.ReferencesNotSent
    };

    /// <summary>
    /// Runs <paramref name="import"/> and reports its outcome, whether it returns a result or throws.
    /// </summary>
    /// <param name="import">The import to run.</param>
    /// <param name="operation">"Import" or "Copy", as the messages name the operation.</param>
    /// <param name="progress">The command's progress reporter.</param>
    /// <param name="error">Where the text lines go (stderr).</param>
    /// <param name="debug">Whether to write the stack trace of an unexpected exception.</param>
    internal static async Task<int> RunAsync(
        Func<Task<ImportResult>> import,
        string operation,
        IProgressReporter progress,
        TextWriter error,
        bool debug)
    {
        ImportResult result;
        try
        {
            result = await import().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Report(ex, operation, progress, error, debug);
        }

        return Report(result, error);
    }

    /// <summary>
    /// Reports a finished import: the warnings about data not written, the plug-in steps left
    /// disabled, and exit code 0 only for a successful import (2 otherwise).
    /// </summary>
    internal static int Report(ImportResult result, TextWriter error)
    {
        foreach (var warning in result.Warnings.Where(w => PrintedWarningCodes.Contains(w.Code)))
        {
            error.WriteLine($"Warning: {warning.Message}");
        }

        WritePluginStepsLeftDisabled(error, result.PluginStepsLeftDisabled);
        return result.Success ? ExitCodes.Success : ExitCodes.Failure;
    }

    /// <summary>
    /// Reports an import that ended with an exception: a cancellation (with the steps it left
    /// disabled), a refusal (with the steps left disabled and a step in an unknown state), a
    /// <see cref="PpdsException"/> (a schema refusal: exit 3), a schema mismatch, or anything else.
    /// </summary>
    internal static int Report(Exception ex, string operation, IProgressReporter progress, TextWriter error, bool debug)
    {
        switch (ex)
        {
            case ImportCancelledException cancelled:
                progress.Error(new OperationCanceledException(), $"{operation} cancelled by user.");
                WritePluginStepsLeftDisabled(error, cancelled.PluginStepsLeftDisabled);
                return ExitCodes.Failure;

            case OperationCanceledException:
                progress.Error(new OperationCanceledException(), $"{operation} cancelled by user.");
                return ExitCodes.Failure;

            case SchemaMismatchException mismatch:
                error.WriteLine();
                error.WriteLine(mismatch.Message);
                return ExitCodes.Failure;

            case ImportRefusedException refused:
                error.WriteLine();
                error.WriteLine($"Error: {refused.Message}");
                WritePluginStepsLeftDisabled(error, refused.PluginStepsLeftDisabled);
                if (refused.PluginStepInUnknownState is { } unknown)
                {
                    error.WriteLine($"Check this plug-in step in the target: it could not be disabled and its re-enable failed, so its state is unknown:");
                    error.WriteLine($"  - {Name(unknown)} ({unknown.Id})");
                }
                return ExceptionMapper.ToExitCode(refused);

            case PpdsException ppds:
                error.WriteLine();
                error.WriteLine($"Error: {ppds.UserMessage}");
                return ExceptionMapper.ToExitCode(ppds);

            default:
                progress.Error(ex, $"{operation} failed");
                if (debug)
                {
                    error.WriteLine(ex.StackTrace);
                }
                return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Writes the plug-in steps an import disabled and could not re-enable, each with its id, so
    /// they can be re-enabled by hand. Writes nothing when there are none.
    /// </summary>
    internal static void WritePluginStepsLeftDisabled(TextWriter error, IReadOnlyList<PluginStepInfo> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        error.WriteLine();
        error.WriteLine($"Warning: {steps.Count} plug-in step(s) that this import disabled could not be re-enabled and remain disabled in the target:");
        foreach (var step in steps)
        {
            error.WriteLine($"  - {Name(step)} ({step.Id})");
        }
        error.WriteLine("Re-enable them in the target (for example in the Plug-in Registration Tool).");
    }

    private static string Name(PluginStepInfo step) => string.IsNullOrEmpty(step.Name) ? "(unnamed step)" : step.Name;
}
