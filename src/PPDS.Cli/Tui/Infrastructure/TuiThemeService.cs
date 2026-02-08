using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Default implementation of <see cref="ITuiThemeService"/>.
/// Provides environment detection and color scheme selection.
/// </summary>
public sealed class TuiThemeService : ITuiThemeService
{
    private readonly IEnvironmentConfigService? _configService;

    public TuiThemeService(IEnvironmentConfigService? configService = null)
    {
        _configService = configService;
    }

    /// <inheritdoc />
    public EnvironmentType DetectEnvironmentType(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            return EnvironmentType.Unknown;
        }

        var url = environmentUrl.ToLowerInvariant();

        // Only use keyword detection from the org name portion of the URL.
        // The CRM regional suffix (crm, crm2, crm4, etc.) indicates geographic
        // region, NOT environment type — do not use it for classification.
        if (ContainsDevKeyword(url))
        {
            return EnvironmentType.Development;
        }

        if (ContainsTrialKeyword(url))
        {
            return EnvironmentType.Trial;
        }

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

    /// <inheritdoc />
    public ColorScheme GetStatusBarSchemeForUrl(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return TuiColorPalette.StatusBar_Default;

        if (_configService != null)
        {
            // Terminal.Gui Redraw() must be synchronous; config store is cached after first load
#pragma warning disable PPDS012
            var color = _configService.ResolveColorAsync(environmentUrl).GetAwaiter().GetResult();
#pragma warning restore PPDS012
            return TuiColorPalette.GetStatusBarScheme(color);
        }

        var envType = DetectEnvironmentType(environmentUrl);
        return TuiColorPalette.GetStatusBarScheme(envType);
    }

    /// <inheritdoc />
    public string GetEnvironmentLabelForUrl(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return "";

        if (_configService != null)
        {
            // Terminal.Gui UI thread must be synchronous; config store is cached after first load
#pragma warning disable PPDS012
            var type = _configService.ResolveTypeAsync(environmentUrl).GetAwaiter().GetResult();
#pragma warning restore PPDS012
            return type?.ToUpperInvariant() switch
            {
                "PRODUCTION" => "PROD",
                "DEVELOPMENT" => "DEV",
                var t when t != null && t.Length <= 8 => t,
                var t when t != null => t[..8],
                _ => ""
            };
        }

        return GetEnvironmentLabel(DetectEnvironmentType(environmentUrl));
    }

    /// <inheritdoc />
    public EnvironmentColor GetResolvedColor(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return EnvironmentColor.Gray;

        if (_configService != null)
        {
            // Terminal.Gui UI thread must be synchronous; config store is cached after first load
#pragma warning disable PPDS012
            return _configService.ResolveColorAsync(environmentUrl).GetAwaiter().GetResult();
#pragma warning restore PPDS012
        }

        var envType = DetectEnvironmentType(environmentUrl);
        return envType switch
        {
            EnvironmentType.Production => EnvironmentColor.Red,
            EnvironmentType.Sandbox => EnvironmentColor.Brown,
            EnvironmentType.Development => EnvironmentColor.Green,
            EnvironmentType.Trial => EnvironmentColor.Cyan,
            _ => EnvironmentColor.Gray
        };
    }

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
