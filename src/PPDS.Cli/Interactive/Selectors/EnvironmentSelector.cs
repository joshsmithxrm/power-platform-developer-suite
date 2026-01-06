using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Interactive.Components;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Selectors;

/// <summary>
/// Interactive environment selection.
/// </summary>
internal static class EnvironmentSelector
{
    /// <summary>
    /// Result of an environment selection operation.
    /// </summary>
    public sealed class SelectionResult
    {
        public bool Changed { get; init; }
        public bool Cancelled { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Represents an environment choice in the selection prompt.
    /// </summary>
    private sealed class EnvironmentChoice
    {
        public DiscoveredEnvironment? Environment { get; init; }
        public bool IsBack { get; init; }

        public override string ToString()
        {
            if (IsBack) return "[Back]";
            return Environment?.FriendlyName ?? "(unknown)";
        }
    }

    /// <summary>
    /// Shows the environment selector and allows the user to switch environments.
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
        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            return new SelectionResult
            {
                ErrorMessage = "No active profile. Create a profile first."
            };
        }

        // Check if this profile supports global discovery
        if (!GlobalDiscoveryService.SupportsGlobalDiscovery(profile.AuthMethod))
        {
            return await ShowManualEntryAsync(store, collection, profile, cancellationToken);
        }

        // Discover environments
        IReadOnlyList<DiscoveredEnvironment> environments;
        try
        {
            environments = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Styles.Primary)
                .StartAsync("Discovering environments...", async ctx =>
                {
                    using var gds = GlobalDiscoveryService.FromProfile(profile);
                    return await gds.DiscoverEnvironmentsAsync(cancellationToken);
                });
        }
        catch (Exception ex)
        {
            return new SelectionResult
            {
                ErrorMessage = $"Failed to discover environments: {ex.Message}"
            };
        }

        if (environments.Count == 0)
        {
            AnsiConsole.MarkupLine(Styles.WarningText("No environments found for this profile."));
            return new SelectionResult { Cancelled = true };
        }

        return await ShowEnvironmentListAsync(store, collection, profile, environments, cancellationToken);
    }

    private static async Task<SelectionResult> ShowEnvironmentListAsync(
        ProfileStore store,
        ProfileCollection collection,
        AuthProfile profile,
        IReadOnlyList<DiscoveredEnvironment> environments,
        CancellationToken cancellationToken)
    {
        var choices = BuildChoices(environments, profile);

        var prompt = new SelectionPrompt<EnvironmentChoice>()
            .Title($"[grey]Select an environment ({environments.Count} available)[/]")
            .PageSize(15)
            .HighlightStyle(Styles.SelectionHighlight)
            .AddChoices(choices)
            .UseConverter(choice => FormatChoice(choice, profile));

        try
        {
            var selected = AnsiConsole.Prompt(prompt);

            if (selected.IsBack)
            {
                return new SelectionResult { Cancelled = true };
            }

            if (selected.Environment != null)
            {
                var env = selected.Environment;

                // Check if already selected
                var currentUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();
                var selectedUrl = env.ApiUrl.TrimEnd('/').ToLowerInvariant();

                if (currentUrl == selectedUrl)
                {
                    AnsiConsole.MarkupLine(Styles.MutedText($"'{env.FriendlyName}' is already the active environment."));
                    return new SelectionResult { Changed = false };
                }

                // Update profile with new environment
                profile.Environment = new EnvironmentInfo
                {
                    Url = env.ApiUrl,
                    DisplayName = env.FriendlyName,
                    UniqueName = env.UniqueName,
                    EnvironmentId = env.EnvironmentId,
                    OrganizationId = env.Id.ToString(),
                    Type = env.EnvironmentType,
                    Region = env.Region
                };

                await store.SaveAsync(collection, cancellationToken);

                AnsiConsole.MarkupLine(Styles.SuccessText($"Switched to environment: {env.FriendlyName}"));
                return new SelectionResult { Changed = true };
            }
        }
        catch (OperationCanceledException)
        {
            return new SelectionResult { Cancelled = true };
        }

        return new SelectionResult { Cancelled = true };
    }

    private static async Task<SelectionResult> ShowManualEntryAsync(
        ProfileStore store,
        ProfileCollection collection,
        AuthProfile profile,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(Styles.WarningText(
            $"Global Discovery is not supported for {profile.AuthMethod} authentication."));
        AnsiConsole.MarkupLine(Styles.MutedText(
            "Enter the environment URL directly (e.g., https://org.crm.dynamics.com):"));
        AnsiConsole.WriteLine();

        var urlPrompt = new TextPrompt<string>("[grey]Environment URL:[/]")
            .AllowEmpty()
            .Validate(url =>
            {
                if (string.IsNullOrWhiteSpace(url))
                    return ValidationResult.Success();

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return ValidationResult.Error("Invalid URL format");

                if (uri.Scheme != "https")
                    return ValidationResult.Error("URL must use HTTPS");

                return ValidationResult.Success();
            });

        try
        {
            var url = AnsiConsole.Prompt(urlPrompt);

            if (string.IsNullOrWhiteSpace(url))
            {
                return new SelectionResult { Cancelled = true };
            }

            // Extract display name from URL
            var displayName = ExtractEnvironmentName(url);

            profile.Environment = new EnvironmentInfo
            {
                Url = url.TrimEnd('/'),
                DisplayName = displayName
            };

            await store.SaveAsync(collection, cancellationToken);

            AnsiConsole.MarkupLine(Styles.SuccessText($"Environment set to: {displayName}"));
            return new SelectionResult { Changed = true };
        }
        catch (OperationCanceledException)
        {
            return new SelectionResult { Cancelled = true };
        }
    }

    private static List<EnvironmentChoice> BuildChoices(
        IReadOnlyList<DiscoveredEnvironment> environments,
        AuthProfile profile)
    {
        var choices = new List<EnvironmentChoice>();

        // Sort environments: current first, then by name
        var currentUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();
        var sorted = environments
            .OrderByDescending(e => e.ApiUrl.TrimEnd('/').ToLowerInvariant() == currentUrl)
            .ThenBy(e => e.FriendlyName);

        foreach (var env in sorted)
        {
            choices.Add(new EnvironmentChoice { Environment = env });
        }

        // Add back option
        choices.Add(new EnvironmentChoice { IsBack = true });

        return choices;
    }

    private static string FormatChoice(EnvironmentChoice choice, AuthProfile profile)
    {
        if (choice.IsBack)
        {
            return Styles.MutedText("[Back]");
        }

        if (choice.Environment == null)
        {
            return "(unknown)";
        }

        var env = choice.Environment;
        var parts = new List<string>();

        // Check if this is the current environment
        var currentUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();
        var isActive = env.ApiUrl.TrimEnd('/').ToLowerInvariant() == currentUrl;

        // Name (escape for markup safety)
        if (isActive)
        {
            parts.Add(Styles.SuccessText(env.FriendlyName));
            parts.Add(Styles.SuccessText("*"));
        }
        else
        {
            parts.Add(Markup.Escape(env.FriendlyName));
        }

        // Type
        parts.Add(Styles.MutedText($"({env.EnvironmentType})"));

        // Region if available
        if (!string.IsNullOrEmpty(env.Region))
        {
            parts.Add(Styles.MutedText($"[{env.Region}]"));
        }

        return string.Join(" ", parts);
    }

    private static string ExtractEnvironmentName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // Extract org name from host (e.g., "myorg" from "myorg.crm.dynamics.com")
        var parts = uri.Host.Split('.');
        return parts.Length > 0 ? parts[0] : url;
    }
}
