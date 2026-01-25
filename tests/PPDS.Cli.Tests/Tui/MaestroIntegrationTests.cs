using System.Threading.Tasks;
using Maestro.Abstractions;
using Maestro.TerminalGui;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

public class MaestroIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerifyMaestroInjection()
    {
        // Setup
        using var app = MaestroApplication.Create();
        app.Init(new FakeDriver());
        var window = new Window("Test");
        var textField = new TextField("") { Width = 10 };
        window.Add(textField);

        // Run test logic in background while UI runs on main thread
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500); // Wait for UI to stabilize

                // 1. Capture Tree
                var tree = await app.Driver.CaptureTreeAsync();

                // 2. Find Element (Role.TextBox for TextFields in V1)
                var target = tree.FindFirst(e => e.Role == ElementRole.TextBox);
                Assert.NotNull(target);

                // 3. Inject Input
                await app.Driver.TypeAsync(target, "Success");

                // 4. Verify
                var updatedTree = await app.Driver.CaptureTreeAsync();
                var updatedTarget = updatedTree.FindFirst(e => e.Role == ElementRole.TextBox);
                
                Assert.NotNull(updatedTarget);
                Assert.Equal("Success", updatedTarget.Value);
            }
            finally
            {
                app.RequestStop();
            }
        });

        // Run Application
        app.Run(window);
    }
}
