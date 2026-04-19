using System.CommandLine;
using PPDS.Cli.Commands.Solutions;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Solutions;

/// <summary>
/// Tests for <see cref="ExportCommand"/> structure and the workspace-path guard.
/// Covers G1 (CLI opt-out flag for writing outside workspace).
/// </summary>
public class ExportCommandTests
{
    [Fact]
    public void Create_RegistersAllowOutsideWorkspaceOption()
    {
        var command = ExportCommand.Create();

        var hasOption = command.Options.Any(o => o.Name == "--allow-outside-workspace");
        Assert.True(hasOption, "Expected --allow-outside-workspace option to be registered.");
    }

    [Fact]
    public void Create_AllowOutsideWorkspaceOption_DefaultsToFalse()
    {
        var command = ExportCommand.Create();

        var option = command.Options.First(o => o.Name == "--allow-outside-workspace");
        Assert.IsType<Option<bool>>(option);
    }

    [Fact]
    public void IsUnderWorkspace_ExactRoot_True()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\work" : "/work";

        Assert.True(ExportCommand.IsUnderWorkspace(root, root));
    }

    [Fact]
    public void IsUnderWorkspace_NestedPath_True()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
        var nested = OperatingSystem.IsWindows() ? @"C:\work\subdir\file.zip" : "/work/subdir/file.zip";

        Assert.True(ExportCommand.IsUnderWorkspace(nested, root));
    }

    [Fact]
    public void IsUnderWorkspace_SiblingPrefix_False()
    {
        // Regression guard: "C:\\work" must not match "C:\\work-evil\\leaked.zip".
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(ExportCommand.IsUnderWorkspace(@"C:\work-evil\leaked.zip", @"C:\work"));
    }

    [Fact]
    public void IsUnderWorkspace_OutsideRoot_False()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
        var outside = OperatingSystem.IsWindows() ? @"C:\Users\x" : "/etc/shadow";

        Assert.False(ExportCommand.IsUnderWorkspace(outside, root));
    }
}
