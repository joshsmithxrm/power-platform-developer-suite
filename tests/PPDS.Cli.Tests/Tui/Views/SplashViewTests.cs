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
        Assert.True(state.SpinnerActive);
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
        Assert.False(state.SpinnerActive);
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

    [Fact]
    public void SetReady_AfterSetStatus_TransitionsCorrectly()
    {
        var splash = new SplashView();

        // Simulate init progress
        splash.SetStatus("Connecting...");
        var midState = splash.CaptureState();
        Assert.Equal("Connecting...", midState.StatusMessage);
        Assert.False(midState.IsReady);

        // Simulate init complete
        splash.SetReady();
        var readyState = splash.CaptureState();
        Assert.True(readyState.IsReady);
        Assert.False(readyState.SpinnerActive);
        Assert.Equal("Ready", readyState.StatusMessage);
    }

    [Fact]
    public void SetStatus_AfterSetReady_IsIgnored()
    {
        var splash = new SplashView();

        splash.SetReady();
        splash.SetStatus("Should not revert ready");
        var state = splash.CaptureState();

        // SetStatus is a no-op after SetReady — message stays "Ready"
        Assert.True(state.IsReady);
        Assert.Equal("Ready", state.StatusMessage);
    }
}
