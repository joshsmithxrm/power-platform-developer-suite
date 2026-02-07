using PPDS.Cli.Tui.Views;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

[Trait("Category", "TuiUnit")]
public sealed class SplashViewTests
{
    [Fact]
    public void InitialState_ShowsConnecting()
    {
        var splash = new SplashView();
        var state = splash.CaptureState();

        Assert.False(state.IsReady);
        Assert.NotNull(state.Version);
        Assert.NotEmpty(state.StatusMessage);
    }

    [Fact]
    public void SetStatus_UpdatesMessage()
    {
        var splash = new SplashView();

        splash.SetStatus("Loading profile...");
        var state = splash.CaptureState();

        Assert.Equal("Loading profile...", state.StatusMessage);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void SetReady_MarksReady()
    {
        var splash = new SplashView();

        splash.SetReady();
        var state = splash.CaptureState();

        Assert.True(state.IsReady);
    }

    [Fact]
    public void Version_MatchesAssemblyVersion()
    {
        var splash = new SplashView();
        var state = splash.CaptureState();

        Assert.NotNull(state.Version);
        // Version should contain at least a major.minor pattern
        Assert.Matches(@"\d+\.\d+", state.Version);
    }
}
