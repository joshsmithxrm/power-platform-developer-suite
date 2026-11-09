using System.CommandLine;
using System.CommandLine.Parsing;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Adds compatible environment aliases and explains the future entity-alias migration.
/// Existing option names and aliases keep their current meanings.
/// </summary>
internal static class EnvironmentAliasPolicy
{
    private const string Notice = "warning: '-e' for --entity/--entities is deprecated; use the long option. Use --environment or -env for environments. Existing -e meanings are retained in this release.";

    internal static void Apply(Command root)
    {
        var seen = new HashSet<Option>();
        foreach (var command in Commands(root))
        {
            foreach (var option in command.Options)
            {
                if (!seen.Add(option))
                {
                    continue;
                }

                if (option.Name == "--environment" && !option.Aliases.Contains("-env"))
                {
                    option.Aliases.Add("-env");
                }

                if (IsEntityOption(option) && option.Aliases.Contains("-e"))
                {
                    var suffix = $" (-e is deprecated; use {option.Name}.)";
                    if (!(option.Description ?? "").EndsWith(suffix, StringComparison.Ordinal))
                    {
                        option.Description += suffix;
                    }
                }
            }
        }
    }

    internal static void WarnIfDeprecatedEntityAliasUsed(ParseResult parseResult) =>
        WarnIfDeprecatedEntityAliasUsed(parseResult, Console.Error);

    internal static void WarnIfDeprecatedEntityAliasUsed(ParseResult parseResult, TextWriter writer)
    {
        // IdentifierToken proves the actual binding without accessing Token.Symbol.
        // It identifies the first occurrence, so mixed repeated aliases also need the
        // public token type plus the selected command's unambiguous visible binding.
        var results = CommandResults(parseResult.CommandResult)
            .SelectMany(result => result.Children.OfType<OptionResult>())
            .Where(result => !result.Implicit && IsEntityOption(result.Option)
                && result.Option.Aliases.Contains("-e"))
            .ToArray();
        var firstUse = results.Any(result => result.IdentifierToken is
            { Type: TokenType.Option, Value: "-e" });
        var visible = VisibleLegacyOptions(parseResult.CommandResult).Distinct().ToArray();
        var repeatedUse = visible.Length == 1 && IsEntityOption(visible[0])
            && results.Any(result => ReferenceEquals(result.Option, visible[0])
                && result.IdentifierTokenCount > 1)
            && parseResult.Tokens.Any(token => token.Type == TokenType.Option && token.Value == "-e");
        if (firstUse || repeatedUse)
        {
            writer.WriteLine(Notice);
        }
    }

    private static bool IsEntityOption(Option option) => option.Name is "--entity" or "--entities";

    private static IEnumerable<Option> VisibleLegacyOptions(CommandResult selected)
    {
        var leaf = true;
        foreach (var result in CommandResults(selected))
        {
            foreach (var option in result.Command.Options.Where(option =>
                (leaf || option.Recursive) && (option.Name == "-e" || option.Aliases.Contains("-e"))))
            {
                yield return option;
            }
            leaf = false;
        }
    }

    private static IEnumerable<CommandResult> CommandResults(CommandResult selected)
    {
        for (CommandResult? result = selected; result is not null; result = result.Parent as CommandResult)
        {
            yield return result;
        }
    }

    private static IEnumerable<Command> Commands(Command root)
    {
        yield return root;
        foreach (var child in root.Subcommands)
        {
            foreach (var command in Commands(child))
            {
                yield return command;
            }
        }
    }
}
