namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Shared discovery of the four generator fixture roots. Centralised so the
/// MDX and terminology tests agree on exactly which .md files they scan —
/// if a new generator lands it only needs to be registered here.
/// </summary>
internal static class WorkflowFixtures
{
    private static readonly string[] FixtureProjects =
    {
        "PPDS.DocsGen.Cli.Tests",
        "PPDS.DocsGen.Libs.Tests",
        "PPDS.DocsGen.Mcp.Tests",
        "PPDS.DocsGen.Extension.Tests",
    };

    /// <summary>
    /// Absolute paths to every generator's fixture Expected directory that
    /// currently exists on disk. The Extension generator uses lowercase
    /// `fixtures/ext-reflect/expected` rather than `Fixtures/Expected`; we
    /// probe both conventions so either shape counts.
    /// </summary>
    public static IReadOnlyList<string> ExpectedRoots(string repoRoot)
    {
        var candidates = new List<string>();
        foreach (var project in FixtureProjects)
        {
            var pascal = Path.Combine(repoRoot, "tests", project, "Fixtures", "Expected");
            if (Directory.Exists(pascal))
            {
                candidates.Add(pascal);
                continue;
            }

            // Extension project uses TypeScript convention.
            var lower = Path.Combine(repoRoot, "tests", project, "fixtures", "ext-reflect", "expected");
            if (Directory.Exists(lower))
            {
                candidates.Add(lower);
            }
        }

        return candidates;
    }
}
