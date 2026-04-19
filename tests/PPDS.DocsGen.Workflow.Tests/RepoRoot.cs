namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Walks parent directories from the test assembly's output folder until
/// PPDS.sln is found. The test project intentionally does not reference
/// the generator projects, so fixture .md paths can't be resolved via
/// ProjectReference — this is how we bridge that gap.
/// </summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate PPDS.sln above {AppContext.BaseDirectory}");
    }
}
