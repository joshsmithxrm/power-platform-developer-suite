namespace PPDS.Cli.Tests.TestHelpers;

using System.IO;

public static class PathHelpers
{
    /// <summary>
    /// Walks up from AppContext.BaseDirectory to find the repo root (folder containing .claude/).
    /// </summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".claude")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
            throw new InvalidOperationException("Could not locate repo root (no .claude/ directory found).");
        return dir.FullName;
    }
}
