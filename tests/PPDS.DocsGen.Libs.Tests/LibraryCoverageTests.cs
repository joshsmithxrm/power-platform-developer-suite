using Xunit;

namespace PPDS.DocsGen.Libs.Tests;

public sealed class LibraryCoverageTests
{
    [Fact]
    public void CompleteCoverageAndFailureBoundaries()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "FixtureAssembly", "PPDS.DocsGen.Libs.Tests.FixtureLib.dll");
        var workspace = Path.Combine(Path.GetTempPath(), "ppds-library-coverage-" + Guid.NewGuid().ToString("N"));
        try { LibraryCoverageFixtureChecks.Run(fixture, workspace); }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive:true); }
    }
}
