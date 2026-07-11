using System.CommandLine;
using System.CommandLine.Parsing;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Emits a one-line deprecation warning when a command was invoked via a deprecated alias
/// (e.g. <c>ppds plugintraces list</c> instead of the canonical <c>ppds plugin-traces list</c>).
/// </summary>
/// <remarks>
/// This is deliberately generic rather than a hardcoded old-to-new name map: it walks the
/// matched <see cref="CommandResult"/> chain produced by parsing and, for each matched command,
/// compares the token the user actually typed (<see cref="CommandResult.IdentifierToken"/>)
/// against the command's canonical <see cref="Command.Name"/>. If they differ, the typed token
/// must have matched one of the command's <see cref="Command.Aliases"/>, which is exactly the
/// "invoked via deprecated alias" case. Any command that registers an alias via
/// <c>command.Aliases.Add("old-name")</c> is automatically covered — no map to keep in sync.
///
/// Introduced for #1246 (kebab-case command renames: plugin-traces, environment-variables,
/// connection-references, import-jobs). Per I1 in specs/CONSTITUTION.md, this writes to
/// stderr only — stdout is reserved for data.
/// </remarks>
public static class CommandAliasDeprecation
{
    /// <summary>
    /// Inspects the parsed command chain and writes exactly one deprecation warning to stderr
    /// if any matched command was reached through an alias rather than its canonical name.
    /// No-op (prints nothing) when every matched command was invoked by its canonical name.
    /// </summary>
    public static void WarnIfDeprecatedAliasUsed(ParseResult parseResult) =>
        WarnIfDeprecatedAliasUsed(parseResult, Console.Error);

    /// <summary>
    /// Overload accepting an explicit <see cref="TextWriter"/>. Production code should use the
    /// single-argument overload (always stderr, per I1 in specs/CONSTITUTION.md); this overload
    /// exists so tests can assert on output via an isolated <see cref="StringWriter"/> instead of
    /// swapping the process-wide <see cref="Console.Error"/>, which is unsafe under xUnit's
    /// default cross-class test parallelization (observed flaky when many test classes redirect
    /// <c>Console.Error</c> concurrently).
    /// </summary>
    public static void WarnIfDeprecatedAliasUsed(ParseResult parseResult, TextWriter writer)
    {
        for (var result = parseResult.CommandResult; result is not null; result = result.Parent as CommandResult)
        {
            var typedToken = result.IdentifierToken.Value;

            if (!string.Equals(typedToken, result.Command.Name, StringComparison.Ordinal)
                && result.Command.Aliases.Contains(typedToken))
            {
                writer.WriteLine($"warning: '{typedToken}' is deprecated; use '{result.Command.Name}'");
                return; // exactly one line, even if somehow nested
            }
        }
    }
}
