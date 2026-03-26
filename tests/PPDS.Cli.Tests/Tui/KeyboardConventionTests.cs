using Xunit;

namespace PPDS.Cli.Tests.Tui;

[Trait("Category", "TuiUnit")]
public class KeyboardConventionTests
{
    [Fact]
    public void WebResources_NoCtrlT()
    {
        var srcDir = FindSrcDirectory();
        var file = Path.Combine(srcDir, "PPDS.Cli", "Tui", "Screens", "WebResourcesScreen.cs");
        var content = File.ReadAllText(file);

        // Must NOT contain the old Ctrl+T (without Shift)
        // The pattern "Key.CtrlMask | Key.T" without ShiftMask is the violation
        var lines = File.ReadAllLines(file);
        var violations = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Match "Key.CtrlMask | Key.T" but NOT "Key.CtrlMask | Key.ShiftMask | Key.T"
            if (line.Contains("Key.CtrlMask | Key.T") && !line.Contains("Key.ShiftMask"))
            {
                violations.Add($"Line {i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"WebResourcesScreen still registers Ctrl+T (should be Ctrl+Shift+T):\n{string.Join("\n", violations)}");

        // Must contain the new Ctrl+Shift+T
        Assert.Contains("Key.CtrlMask | Key.ShiftMask | Key.T", content);
    }

    [Fact]
    public void PluginTraces_NoCtrlT()
    {
        var srcDir = FindSrcDirectory();
        var file = Path.Combine(srcDir, "PPDS.Cli", "Tui", "Screens", "PluginTracesScreen.cs");
        var content = File.ReadAllText(file);

        // Must NOT contain Ctrl+T at all
        var lines = File.ReadAllLines(file);
        var violations = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("Key.CtrlMask | Key.T"))
            {
                violations.Add($"Line {i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"PluginTracesScreen still registers Ctrl+T (should be F6):\n{string.Join("\n", violations)}");

        // Must contain F6
        Assert.Contains("Key.F6", content);
    }

    private static string FindSrcDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate))
                return srcCandidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find src/ directory");
    }
}
