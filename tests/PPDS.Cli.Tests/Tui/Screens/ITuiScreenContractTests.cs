using PPDS.Cli.Tui.Screens;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// Tests for ITuiScreen interface contract.
/// These ensure the interface has the expected members for screen-shell communication.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class ITuiScreenContractTests
{
    [Fact]
    public void ITuiScreen_HasMenuStateChangedEvent()
    {
        // This test verifies the interface contract at compile time.
        // If MenuStateChanged event is removed from ITuiScreen, this test fails to compile.
        // This prevents regression of the File > Export menu bug fix.

        var eventInfo = typeof(ITuiScreen).GetEvent(nameof(ITuiScreen.MenuStateChanged));

        Assert.NotNull(eventInfo);
        Assert.Equal(typeof(Action), eventInfo.EventHandlerType);
    }

    [Fact]
    public void ITuiScreen_HasCloseRequestedEvent()
    {
        // Verify the existing event is still present
        var eventInfo = typeof(ITuiScreen).GetEvent(nameof(ITuiScreen.CloseRequested));

        Assert.NotNull(eventInfo);
        Assert.Equal(typeof(Action), eventInfo.EventHandlerType);
    }

    [Fact]
    public void ITuiScreen_HasExportActionProperty()
    {
        // Verify the ExportAction property exists
        var propInfo = typeof(ITuiScreen).GetProperty(nameof(ITuiScreen.ExportAction));

        Assert.NotNull(propInfo);
        Assert.Equal(typeof(Action), propInfo.PropertyType);
    }
}
