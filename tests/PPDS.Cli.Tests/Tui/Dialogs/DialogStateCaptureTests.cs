using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

/// <summary>
/// Unit tests for dialog state capture functionality.
/// </summary>
/// <remarks>
/// These tests verify that dialogs correctly implement ITuiStateCapture,
/// allowing their state to be captured and verified without requiring
/// Terminal.Gui runtime. Tests can run in CI/CD pipelines.
///
/// Pattern: Create dialog -> Capture state -> Verify state properties
/// </remarks>
[Trait("Category", "TuiUnit")]
public class DialogStateCaptureTests
{
    #region AboutDialog Tests

    [Fact]
    public void AboutDialog_CaptureState_ReturnsValidState()
    {
        using var dialog = new AboutDialog();

        var state = dialog.CaptureState();

        Assert.Equal("About PPDS", state.Title);
        Assert.Equal("Power Platform Developer Suite", state.ProductName);
        Assert.NotEmpty(state.Version);
        Assert.NotNull(state.Description);
        Assert.NotNull(state.GitHubUrl);
        Assert.Contains("github.com", state.GitHubUrl);
    }

    #endregion

    #region KeyboardShortcutsDialog Tests

    [Fact]
    public void KeyboardShortcutsDialog_CaptureState_WithoutRegistry_ContainsBuiltInShortcuts()
    {
        using var dialog = new KeyboardShortcutsDialog();

        var state = dialog.CaptureState();

        Assert.Equal("Keyboard Shortcuts", state.Title);
        Assert.NotEmpty(state.Shortcuts);
        Assert.True(state.ShortcutCount > 0);
        // Built-in table navigation shortcuts are always present
        Assert.Contains(state.Shortcuts, s => s.Key == "Arrows" && s.Scope == "Table Navigation");
    }

    [Fact]
    public void KeyboardShortcutsDialog_CaptureState_ContainsRegisteredGlobalShortcuts()
    {
        var registry = new HotkeyRegistry();
        registry.Register(Key.AltMask | Key.P, HotkeyScope.Global, "Switch profile", () => { });
        registry.Register(Key.AltMask | Key.E, HotkeyScope.Global, "Switch environment", () => { });
        registry.Register(Key.F1, HotkeyScope.Global, "Keyboard shortcuts", () => { });
        registry.Register(Key.F12, HotkeyScope.Global, "Error log", () => { });

        using var dialog = new KeyboardShortcutsDialog(registry);
        var state = dialog.CaptureState();

        Assert.Contains(state.Shortcuts, s => s.Key == "Alt+P" && s.Scope == "Global");
        Assert.Contains(state.Shortcuts, s => s.Key == "Alt+E" && s.Scope == "Global");
        Assert.Contains(state.Shortcuts, s => s.Key == "F1" && s.Scope == "Global");
        Assert.Contains(state.Shortcuts, s => s.Key == "F12" && s.Scope == "Global");
    }

    [Fact]
    public void KeyboardShortcutsDialog_CaptureState_ContainsRegisteredScreenShortcuts()
    {
        var registry = new HotkeyRegistry();
        var owner = new object();
        registry.Register(Key.CtrlMask | Key.Enter, HotkeyScope.Screen, "Execute query", () => { }, owner);
        registry.Register(Key.CtrlMask | Key.E, HotkeyScope.Screen, "Export results", () => { }, owner);

        using var dialog = new KeyboardShortcutsDialog(registry);
        var state = dialog.CaptureState();

        Assert.Contains(state.Shortcuts, s => s.Key == "Ctrl+Enter" && s.Scope == "Screen");
        Assert.Contains(state.Shortcuts, s => s.Key == "Ctrl+E" && s.Scope == "Screen");
    }

    [Fact]
    public void KeyboardShortcutsDialog_CaptureState_ShortcutCountMatchesRegistryPlusBuiltIn()
    {
        var registry = new HotkeyRegistry();
        registry.Register(Key.F1, HotkeyScope.Global, "Help", () => { });
        registry.Register(Key.F12, HotkeyScope.Global, "Error log", () => { });

        using var dialog = new KeyboardShortcutsDialog(registry);
        var state = dialog.CaptureState();

        // 2 registered + 5 built-in table navigation
        Assert.Equal(7, state.ShortcutCount);
    }

    #endregion

    #region PreAuthenticationDialog Tests

    [Fact]
    public void PreAuthenticationDialog_CaptureState_ReturnsValidState()
    {
        using var dialog = new PreAuthenticationDialog();

        var state = dialog.CaptureState();

        Assert.Equal("Authentication Required", state.Title);
        Assert.NotEmpty(state.Message);
        Assert.NotEmpty(state.AvailableOptions);
    }

    [Fact]
    public void PreAuthenticationDialog_CaptureState_WithDeviceCodeCallback_IncludesDeviceCodeOption()
    {
        using var dialog = new PreAuthenticationDialog(deviceCodeCallback: _ => { });

        var state = dialog.CaptureState();

        Assert.Contains("Use Device Code", state.AvailableOptions);
    }

    [Fact]
    public void PreAuthenticationDialog_CaptureState_WithoutCallback_ExcludesDeviceCodeOption()
    {
        using var dialog = new PreAuthenticationDialog(deviceCodeCallback: null);

        var state = dialog.CaptureState();

        Assert.DoesNotContain("Use Device Code", state.AvailableOptions);
    }

    [Fact]
    public void PreAuthenticationDialog_CaptureState_DefaultResultIsCancel()
    {
        using var dialog = new PreAuthenticationDialog();

        var state = dialog.CaptureState();

        Assert.Equal("Cancel", state.SelectedOption);
    }

    #endregion

    #region State Record Tests

    [Fact]
    public void AboutDialogState_RecordEquality_WorksCorrectly()
    {
        var state1 = new AboutDialogState("Title", "Product", "1.0", "Desc", "License", "url");
        var state2 = new AboutDialogState("Title", "Product", "1.0", "Desc", "License", "url");

        Assert.Equal(state1, state2);
    }

    [Fact]
    public void ShortcutEntry_RecordEquality_WorksCorrectly()
    {
        var entry1 = new ShortcutEntry("F1", "Help", "Global");
        var entry2 = new ShortcutEntry("F1", "Help", "Global");

        Assert.Equal(entry1, entry2);
    }

    [Fact]
    public void PreAuthenticationDialogState_RecordEquality_WorksCorrectly()
    {
        var options = new List<string> { "Open Browser", "Cancel" };
        var state1 = new PreAuthenticationDialogState("Title", "Message", "Cancel", options);
        var state2 = new PreAuthenticationDialogState("Title", "Message", "Cancel", options);

        Assert.Equal(state1, state2);
    }

    #endregion
}
