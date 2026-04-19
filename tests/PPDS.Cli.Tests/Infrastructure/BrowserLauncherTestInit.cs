using System.Runtime.CompilerServices;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Swaps <see cref="BrowserHelper.Launcher"/> for a <see cref="NoOpBrowserLauncher"/>
/// before any test executes so the test suite never spawns a real OS browser.
/// See issue #809.
/// </summary>
internal static class BrowserLauncherTestInit
{
    [ModuleInitializer]
    internal static void Install()
    {
        BrowserHelper.Launcher = new NoOpBrowserLauncher();
    }
}
