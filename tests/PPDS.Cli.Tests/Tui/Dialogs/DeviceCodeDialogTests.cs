using PPDS.Cli.Tui.Dialogs;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public class DeviceCodeDialogTests
{
    [Fact]
    public void CaptureState_ContainsUserCode()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();
        Assert.Equal("ABC-DEF", state.UserCode);
    }

    [Fact]
    public void CaptureState_ContainsVerificationUrl()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();
        Assert.Equal("https://microsoft.com/devicelogin", state.VerificationUrl);
    }

    [Fact]
    public void CaptureState_ClipboardCopied_WhenTrue()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin", clipboardCopied: true);
        var state = dialog.CaptureState();
        Assert.True(state.ClipboardCopied);
    }

    [Fact]
    public void CaptureState_ClipboardNotCopied_WhenFalse()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin", clipboardCopied: false);
        var state = dialog.CaptureState();
        Assert.False(state.ClipboardCopied);
    }

    [Fact]
    public void CaptureState_Title_IsAuthenticationRequired()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();
        Assert.Equal("Authentication Required", state.Title);
    }
}
