using System.CommandLine;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Stops advertising dead short aliases on shared global options (#1536).
/// </summary>
/// <remarks>
/// The shared options (<c>--output-format -f</c>, <c>--verbose -v</c>, ...) are single static instances
/// attached to hundreds of commands, so an alias cannot be removed from the instance itself. On a command
/// where a command-specific option registered BEFORE it already binds the same short alias (the parser already
/// gives that option the alias), the shared instance is replaced in that command's option list by a per-command twin that
/// carries only the long name. Which option an alias resolves to never changes; only the dead alias stops
/// appearing in help. The pass walks the whole tree, so a future command that uses the shared
/// <see cref="GlobalOptions"/> instances and has the same clash is covered automatically. Commands that build
/// their own <c>--output-format</c>/<c>--verbose</c> are not touched; a clash there fails the same-command guard
/// test instead.
/// </remarks>
internal static class ShortAliasShadowPolicy
{
    internal static void Apply(Command root)
    {
        foreach (var command in Walk(root))
        {
            for (var i = 0; i < command.Options.Count; i++)
            {
                var option = command.Options[i];
                if (!GlobalOptions.IsShared(option))
                {
                    continue;
                }

                // System.CommandLine gives a duplicated alias to the FIRST-registered option, so the alias is dead on
                // the shared option only when a command-specific owner precedes it. If the shared option comes first
                // it owns the alias: stripping would change parsing, so leave both in place and let the same-command
                // guard test fail loudly instead.
                var shadowed = option.Aliases
                    .Where(alias => IsShort(alias) && command.Options.Take(i).Any(other =>
                        !GlobalOptions.IsShared(other) && Binds(other, alias)))
                    .ToHashSet(StringComparer.Ordinal);
                if (shadowed.Count == 0)
                {
                    continue;
                }

                if (GlobalOptions.CreateTwinWithoutAliases(option, shadowed) is { } twin)
                {
                    command.Options.RemoveAt(i);
                    command.Options.Insert(i, twin);
                }
            }
        }
    }

    private static bool IsShort(string alias) => alias.Length > 1 && alias[0] == '-' && alias[1] != '-';

    private static bool Binds(Option option, string alias) => option.Name == alias || option.Aliases.Contains(alias);

    private static IEnumerable<Command> Walk(Command command)
    {
        yield return command;
        foreach (var child in command.Subcommands)
        {
            foreach (var nested in Walk(child))
            {
                yield return nested;
            }
        }
    }
}
