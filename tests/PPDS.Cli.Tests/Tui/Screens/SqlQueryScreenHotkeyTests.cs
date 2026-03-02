using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// Validates F-key alternatives exist for Linux terminal compatibility.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlQueryScreenHotkeyTests
{
    [Theory]
    [InlineData(Key.F7)]
    [InlineData(Key.F8)]
    [InlineData(Key.F9)]
    public void HotkeyRegistry_FormatsLinuxFKeys_Correctly(Key key)
    {
        var registry = new HotkeyRegistry();
        registry.Register(key, HotkeyScope.Screen, "test action", () => { });
        var bindings = registry.GetAllBindings();
        Assert.Single(bindings);
        Assert.StartsWith("F", HotkeyRegistry.FormatKey(bindings[0].Key));
    }
}
