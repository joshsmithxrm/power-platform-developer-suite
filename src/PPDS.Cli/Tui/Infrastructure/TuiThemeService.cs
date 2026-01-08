using System.Text.RegularExpressions;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Default implementation of <see cref="ITuiThemeService"/>.
/// Provides environment detection and color scheme selection.
/// </summary>
public sealed partial class TuiThemeService : ITuiThemeService
{
    /// <summary>
    /// Regex pattern for sandbox environments (e.g., crm9.dynamics.com, crm11.dynamics.com).
    /// </summary>
    [GeneratedRegex(@"\.crm\d+\.dynamics\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SandboxRegex();

    /// <inheritdoc />
    public EnvironmentType DetectEnvironmentType(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            return EnvironmentType.Unknown;
        }

        var url = environmentUrl.ToLowerInvariant();

        // Check for keywords in the URL/environment name that suggest environment type
        if (ContainsDevKeyword(url))
        {
            return EnvironmentType.Development;
        }

        if (ContainsTrialKeyword(url))
        {
            return EnvironmentType.Trial;
        }

        // Sandbox: regional instances like .crm9.dynamics.com, .crm11.dynamics.com
        // These are typically sandbox/test environments
        if (SandboxRegex().IsMatch(url))
        {
            return EnvironmentType.Sandbox;
        }

        // Production: standard .crm.dynamics.com (no number suffix)
        // This is the default production region
        if (url.Contains(".crm.dynamics.com"))
        {
            return EnvironmentType.Production;
        }

        // Other regional production instances
        // Note: Some regions like .crm4.dynamics.com (EMEA) can be production
        // We default these to Unknown since we can't reliably distinguish
        return EnvironmentType.Unknown;
    }

    /// <inheritdoc />
    public ColorScheme GetStatusBarScheme(EnvironmentType envType)
        => TuiColorPalette.GetStatusBarScheme(envType);

    /// <inheritdoc />
    public ColorScheme GetDefaultScheme()
        => TuiColorPalette.Default;

    /// <inheritdoc />
    public ColorScheme GetErrorScheme()
        => TuiColorPalette.Error;

    /// <inheritdoc />
    public ColorScheme GetSuccessScheme()
        => TuiColorPalette.Success;

    /// <inheritdoc />
    public string GetEnvironmentLabel(EnvironmentType envType) => envType switch
    {
        EnvironmentType.Production => "PROD",
        EnvironmentType.Sandbox => "SANDBOX",
        EnvironmentType.Development => "DEV",
        EnvironmentType.Trial => "TRIAL",
        _ => ""
    };

    #region Keyword Detection

    private static bool ContainsDevKeyword(string url)
    {
        // Common development environment naming patterns
        string[] devKeywords = ["dev", "develop", "development", "test", "qa", "uat"];
        return devKeywords.Any(keyword =>
            url.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsTrialKeyword(string url)
    {
        // Trial environment indicators
        string[] trialKeywords = ["trial", "demo", "preview"];
        return trialKeywords.Any(keyword =>
            url.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
