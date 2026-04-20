namespace PPDS.Cli.Tests.Preservation;

using System.IO;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

public class ShakedownSafetyHookPreservationTests
{
    [Fact]
    public void MutationVerbsAndBlockLogic_Unchanged()
    {
        var hookPath = Path.Combine(PathHelpers.RepoRoot(), ".claude", "hooks", "shakedown-safety.py");
        Assert.True(File.Exists(hookPath), $"Hook not found: {hookPath}");
        var content = File.ReadAllText(hookPath);

        var expected = new[]
        {
            "create", "update", "delete", "remove", "import", "apply",
            "register", "unregister", "publish", "truncate", "drop",
            "reset", "set"
        };
        foreach (var verb in expected)
        {
            Assert.Contains($"\"{verb}\"", content);
        }

        Assert.Contains("(\"plugins\", \"deploy\")", content);
        Assert.Contains("BLOCKED [shakedown-safety/readonly]", content);
    }
}
