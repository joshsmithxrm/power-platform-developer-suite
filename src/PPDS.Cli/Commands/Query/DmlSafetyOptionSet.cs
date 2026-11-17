using System.CommandLine;
using PPDS.Cli.Services.Query;

namespace PPDS.Cli.Commands.Query;

/// <summary>
/// The DML safety options shared by every CLI command that executes SQL
/// (<c>query sql</c>, <c>query history execute</c>). Defined once so option names, help text and
/// defaults cannot drift between commands. Each command creates its own instance.
/// </summary>
internal sealed class DmlSafetyOptionSet
{
    /// <summary>Error text for combining <c>--dry-run</c> with CSV output.</summary>
    internal const string DryRunCsvError =
        "CSV output is not supported with --dry-run, which renders a plan rather than a result set. " +
        "Use --output-format Json or Text.";

    public DmlSafetyOptionSet()
    {
        Confirm = new Option<bool>("--confirm")
        {
            Description = "Confirm DML execution without interactive prompt"
        };

        DryRun = new Option<bool>("--dry-run")
        {
            Description = "Show the execution plan without running the DML statement"
        };

        NoLimit = new Option<bool>("--no-limit")
        {
            Description = "Remove the 10,000 row safety cap for DML operations"
        };
    }

    /// <summary>--confirm</summary>
    public Option<bool> Confirm { get; }

    /// <summary>--dry-run</summary>
    public Option<bool> DryRun { get; }

    /// <summary>--no-limit</summary>
    public Option<bool> NoLimit { get; }

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Confirm);
        command.Options.Add(DryRun);
        command.Options.Add(NoLimit);
    }

    /// <summary>Reads the parsed options into the service-level safety options (same defaults everywhere).</summary>
    public DmlSafetyOptions GetOptions(ParseResult parseResult) => new()
    {
        IsConfirmed = parseResult.GetValue(Confirm),
        IsDryRun = parseResult.GetValue(DryRun),
        NoLimit = parseResult.GetValue(NoLimit)
    };
}
