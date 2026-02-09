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

        // Delegate to EnvironmentConfigService for segment-based keyword matching
        var detectedType = EnvironmentConfigService.DetectTypeFromUrl(environmentUrl);
        return detectedType switch
        {
            "Development" => EnvironmentType.Development,
            "Test" => EnvironmentType.Test,
            "Trial" => EnvironmentType.Trial,
            _ => EnvironmentType.Unknown
        };
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
        EnvironmentType.Test => "TEST",
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
            // Priority 1: user-configured custom label
            var label = _configService.ResolveLabelAsync(environmentUrl).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(label))
                return label.Length <= 8 ? label.ToUpperInvariant() : label[..8].ToUpperInvariant();

            // Priority 2: abbreviated type
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

}
