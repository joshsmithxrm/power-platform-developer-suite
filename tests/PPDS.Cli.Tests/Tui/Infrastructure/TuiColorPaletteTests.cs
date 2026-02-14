using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TuiColorPalette"/>.
/// </summary>
/// <remarks>
/// These tests verify color scheme definitions follow the blue background rule.
/// Tests can run without Terminal.Gui context since ColorScheme creation uses fallback.
/// </remarks>
[Trait("Category", "TuiUnit")]
public class TuiColorPaletteTests
{
    /// <summary>
    /// Validates that all color schemes follow the cyan background rule:
    /// When background is Cyan or BrightCyan, foreground MUST be Black.
    /// </summary>
    [Fact]
    public void AllColorSchemes_CyanBackgrounds_MustHaveBlackForeground()
    {
        var violations = TuiColorPalette.ValidateCyanBackgroundRule().ToList();

        if (violations.Count > 0)
        {
            var message = string.Join(Environment.NewLine, violations.Select(v =>
                $"  {v.Scheme}.{v.Attribute}: {v.Foreground} on {v.Background} (should be Black on {v.Background})"));

            Assert.Fail($"Cyan background rule violations found:{Environment.NewLine}{message}");
        }
    }

    /// <summary>
    /// Ensures all color schemes are accessible without Terminal.Gui initialization.
    /// </summary>
    [Fact]
    public void AllColorSchemes_AccessWithoutGuiInit_DoesNotThrow()
    {
        // These should not throw even without Terminal.Gui Application.Init()
        var schemes = new[]
        {
            TuiColorPalette.Default,
            TuiColorPalette.Focused,
            TuiColorPalette.TextInput,
            TuiColorPalette.ReadOnlyText,
            TuiColorPalette.FileDialog,
            TuiColorPalette.StatusBar_Production,
            TuiColorPalette.StatusBar_Sandbox,
            TuiColorPalette.StatusBar_Development,
            TuiColorPalette.StatusBar_Trial,
            TuiColorPalette.StatusBar_Default,
            TuiColorPalette.MenuBar,
            TuiColorPalette.TableHeader,
            TuiColorPalette.Selected,
            TuiColorPalette.Error,
            TuiColorPalette.Success
        };

        foreach (var scheme in schemes)
        {
            Assert.NotNull(scheme);
        }
    }

    #region GetTabScheme Tests

    [Fact]
    public void GetTabScheme_ActiveTab_UsesEnvironmentColorAsBackground()
    {
        // Active tab should use environment color as background with contrasting foreground
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Production, isActive: true);

        Assert.Equal(Color.Red, scheme.Normal.Background);
        Assert.Equal(Color.Black, scheme.Normal.Foreground);
    }

    [Theory]
    [InlineData(EnvironmentType.Production, Color.Red)]
    [InlineData(EnvironmentType.Sandbox, Color.Brown)]
    [InlineData(EnvironmentType.Development, Color.Green)]
    [InlineData(EnvironmentType.Trial, Color.Cyan)]
    [InlineData(EnvironmentType.Unknown, Color.Gray)]
    public void GetTabScheme_ActiveTab_UsesEnvironmentColorBackground(EnvironmentType envType, Color expectedBg)
    {
        var scheme = TuiColorPalette.GetTabScheme(envType, isActive: true);

        Assert.Equal(expectedBg, scheme.Normal.Background);
    }

    [Fact]
    public void GetTabScheme_InactiveProduction_HasRedForeground()
    {
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Production, isActive: false);
        Assert.Equal(Color.Red, scheme.Normal.Foreground);
    }

    [Fact]
    public void GetTabScheme_InactiveSandbox_HasBrightYellowForeground()
    {
        // Brown is hard to read on black, so it uses BrightYellow
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Sandbox, isActive: false);
        Assert.Equal(Color.BrightYellow, scheme.Normal.Foreground);
    }

    [Fact]
    public void GetTabScheme_InactiveDevelopment_HasGreenForeground()
    {
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Development, isActive: false);
        Assert.Equal(Color.Green, scheme.Normal.Foreground);
    }

    [Fact]
    public void GetTabScheme_InactiveTrial_HasCyanForeground()
    {
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Trial, isActive: false);
        Assert.Equal(Color.Cyan, scheme.Normal.Foreground);
    }

    [Fact]
    public void GetTabScheme_InactiveUnknown_HasWhiteForeground()
    {
        // Gray is hard to read on black, so it uses White
        var scheme = TuiColorPalette.GetTabScheme(EnvironmentType.Unknown, isActive: false);
        Assert.Equal(Color.White, scheme.Normal.Foreground);
    }

    [Theory]
    [InlineData(EnvironmentType.Production)]
    [InlineData(EnvironmentType.Sandbox)]
    [InlineData(EnvironmentType.Development)]
    [InlineData(EnvironmentType.Trial)]
    [InlineData(EnvironmentType.Unknown)]
    public void GetTabScheme_AllSchemes_PassCyanBackgroundRule(EnvironmentType envType)
    {
        foreach (var isActive in new[] { true, false })
        {
            var scheme = TuiColorPalette.GetTabScheme(envType, isActive);

            Color[] cyanBackgrounds = { Color.Cyan, Color.BrightCyan };
            var attributes = new[]
            {
                ("Normal", scheme.Normal),
                ("Focus", scheme.Focus),
                ("HotNormal", scheme.HotNormal),
                ("HotFocus", scheme.HotFocus),
                ("Disabled", scheme.Disabled)
            };

            foreach (var (name, attr) in attributes)
            {
                if (cyanBackgrounds.Contains(attr.Background))
                {
                    Assert.True(attr.Foreground == Color.Black,
                        $"GetTabScheme({envType}, active={isActive}).{name}: {attr.Foreground} on {attr.Background} violates cyan background rule");
                }
            }
        }
    }

    #endregion

    [Fact]
    public void TextInput_FocusBackground_DiffersFromNormalBackground()
    {
        var scheme = TuiColorPalette.TextInput;
        Assert.NotEqual(scheme.Normal.Background, scheme.Focus.Background);
    }
}
