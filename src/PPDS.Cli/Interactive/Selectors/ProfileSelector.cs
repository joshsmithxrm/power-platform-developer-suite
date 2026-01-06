using PPDS.Auth.Profiles;
using PPDS.Cli.Interactive.Components;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Selectors;

/// <summary>
/// Interactive profile selection and management.
/// </summary>
internal static class ProfileSelector
{
    /// <summary>
    /// Result of a profile selection operation.
    /// </summary>
    public sealed class SelectionResult
    {
        public bool Changed { get; init; }
        public bool CreateNew { get; init; }
        public bool Cancelled { get; init; }
    }

    /// <summary>
    /// Represents a profile choice in the selection prompt.
    /// </summary>
    private sealed class ProfileChoice
    {
        public AuthProfile? Profile { get; init; }
        public bool IsCreateNew { get; init; }
        public bool IsBack { get; init; }

        public override string ToString()
        {
            if (IsCreateNew) return "[Create New Profile]";
            if (IsBack) return "[Back]";
            return Profile?.DisplayIdentifier ?? "(unknown)";
        }
    }

    /// <summary>
    /// Shows the profile selector and allows the user to switch or create profiles.
    /// </summary>
    /// <param name="store">The profile store.</param>
    /// <param name="collection">The profile collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the selection.</returns>
    public static async Task<SelectionResult> ShowAsync(
        ProfileStore store,
        ProfileCollection collection,
        CancellationToken cancellationToken)
    {
        var choices = BuildChoices(collection);

        var prompt = new SelectionPrompt<ProfileChoice>()
            .Title($"[grey]Select a profile ({collection.Count} available)[/]")
            .PageSize(10)
            .HighlightStyle(Styles.SelectionHighlight)
            .AddChoices(choices)
            .UseConverter(FormatChoice);

        try
        {
            var selected = AnsiConsole.Prompt(prompt);

            if (selected.IsBack)
            {
                return new SelectionResult { Cancelled = true };
            }

            if (selected.IsCreateNew)
            {
                return new SelectionResult { CreateNew = true };
            }

            if (selected.Profile != null)
            {
                // Check if this is already the active profile
                if (collection.ActiveProfile?.Index == selected.Profile.Index)
                {
                    AnsiConsole.MarkupLine(Styles.MutedText($"'{selected.Profile.DisplayIdentifier}' is already the active profile."));
                    return new SelectionResult { Changed = false };
                }

                // Switch to selected profile
                collection.SetActiveByIndex(selected.Profile.Index);
                await store.SaveAsync(collection, cancellationToken);

                AnsiConsole.MarkupLine(Styles.SuccessText($"Switched to profile: {selected.Profile.DisplayIdentifier}"));
                return new SelectionResult { Changed = true };
            }
        }
        catch (OperationCanceledException)
        {
            return new SelectionResult { Cancelled = true };
        }

        return new SelectionResult { Cancelled = true };
    }

    private static List<ProfileChoice> BuildChoices(ProfileCollection collection)
    {
        var choices = new List<ProfileChoice>();

        // Add existing profiles
        foreach (var profile in collection.All)
        {
            choices.Add(new ProfileChoice { Profile = profile });
        }

        // Add create new option
        choices.Add(new ProfileChoice { IsCreateNew = true });

        // Add back option
        choices.Add(new ProfileChoice { IsBack = true });

        return choices;
    }

    private static string FormatChoice(ProfileChoice choice)
    {
        if (choice.IsCreateNew)
        {
            return Styles.PrimaryText("[Create New Profile]");
        }

        if (choice.IsBack)
        {
            return Styles.MutedText("[Back]");
        }

        if (choice.Profile == null)
        {
            return "(unknown)";
        }

        var profile = choice.Profile;
        var parts = new List<string>();

        // Name/index (escape for markup safety)
        parts.Add(Markup.Escape(profile.DisplayIdentifier));

        // Identity
        if (!string.IsNullOrEmpty(profile.IdentityDisplay))
        {
            parts.Add(Styles.MutedText($"({profile.IdentityDisplay})"));
        }

        // Environment hint
        if (profile.HasEnvironment)
        {
            parts.Add(Styles.MutedText($"[{profile.Environment!.DisplayName}]"));
        }

        return string.Join(" ", parts);
    }
}
