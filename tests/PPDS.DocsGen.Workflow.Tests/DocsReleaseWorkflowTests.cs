using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Tests the building blocks invoked by <c>.github/workflows/docs-release.yml</c>.
/// The workflow orchestration is thin shell glue; each test drives the
/// underlying script against a self-contained fixture root so no live
/// GitHub or Dataverse state is touched.
/// </summary>
public class DocsReleaseWorkflowTests
{
    private static string FixtureRoot(string name) =>
        Path.Combine(
            Path.GetDirectoryName(typeof(DocsReleaseWorkflowTests).Assembly.Location)!,
            "Fixtures",
            name);

    /// <summary>
    /// AC-25: exercising the scripts the release workflow glues together
    /// yields the expected file tree and PR-body shape.
    /// </summary>
    /// <remarks>
    /// The fixture contains one ext-reflect input (<c>package.json</c>) and a
    /// minimal PublicAPI baseline for one library ("Auth"). We wire a scratch
    /// git repo so <c>compute-surface-summary.sh</c> has something to diff
    /// against, then drive ext-reflect + compute-surface-summary +
    /// compute-rollover-diff and assert the aggregate artifacts match
    /// expectations.
    /// </remarks>
    [Fact]
    public void DryRunProducesExpectedArtifacts()
    {
        var fakeRepo = ShellRunner.MakeTempDir("dryrun");
        try
        {
            // Layout the scratch repo:
            //   scripts/docs-gen/compute-*.sh  -> symlinked from real scripts dir
            //   src/PPDS.Auth/PublicAPI.*.txt  -> fixture baseline
            //   src/PPDS.Extension/package.json -> fixture
            //
            // We don't actually need to recreate the script — compute-surface-
            // summary takes --root so it works from the fake repo.

            var authDir = Path.Combine(fakeRepo, "src", "PPDS.Auth");
            Directory.CreateDirectory(authDir);

            var shippedAtBaseline = """
#nullable enable
PPDS.Auth.BaselineOne
PPDS.Auth.BaselineTwo

""".ReplaceLineEndings("\n");
            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Shipped.txt"),
                shippedAtBaseline);
            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Unshipped.txt"),
                "#nullable enable\n");

            // Only one of the four libraries is populated; the others are
            // intentionally missing so the summary reports them as "no
            // PublicAPI.Shipped.txt" — a realistic pre-Phase-0 state.

            // Initialize a git repo and commit the baseline.
            ShellRunner.RunGit(fakeRepo, "init", "-q", "-b", "main")
                .ExitCode.Should().Be(0);
            ShellRunner.RunGit(fakeRepo, "add", "-A")
                .ExitCode.Should().Be(0);
            ShellRunner.RunGit(fakeRepo, "commit", "-q", "-m", "baseline")
                .ExitCode.Should().Be(0);
            ShellRunner.RunGit(fakeRepo, "tag", "v1.0.0")
                .ExitCode.Should().Be(0);

            // Now modify: add a new symbol to Unshipped and a deletion from
            // the upcoming Shipped so the summary has a non-trivial shape.
            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Unshipped.txt"),
                "#nullable enable\nPPDS.Auth.FreshType\n");

            // Stage a modified Shipped to represent a "since baseline" delta
            // — remove BaselineTwo, add FreshType, keep BaselineOne.
            var newShipped = """
#nullable enable
PPDS.Auth.BaselineOne
PPDS.Auth.FreshType

""".ReplaceLineEndings("\n");
            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Shipped.txt"),
                newShipped);

            // Run ext-reflect into artifacts/docs/reference/extension.
            var artifactsRoot = Path.Combine(fakeRepo, "artifacts", "docs");
            var extOut = Path.Combine(artifactsRoot, "reference", "extension");
            var packageJson = Path.Combine(FixtureRoot("dryrun"), "package.json");
            File.Exists(packageJson).Should().BeTrue();

            var extResult = ShellRunner.RunNode(
                "scripts/docs-gen/ext-reflect/generate.js",
                new[] { "--package-json", packageJson, "--output", extOut });
            extResult.ExitCode.Should().Be(0,
                because: $"ext-reflect stderr: {extResult.Stderr}");

            Directory.Exists(extOut).Should().BeTrue();

            // Run compute-surface-summary against the fake repo.
            var summaryResult = ShellRunner.RunScript(
                "compute-surface-summary.sh",
                new[] { "--root", fakeRepo, "--since-tag", "v1.0.0" },
                workingDir: fakeRepo);
            summaryResult.ExitCode.Should().Be(0,
                because: $"summary stderr: {summaryResult.Stderr}");
            var prBody = summaryResult.Stdout;

            // Run compute-rollover-diff — moves FreshType from Unshipped to
            // Shipped (Shipped keeps FreshType since it was already there,
            // but the dedupe is the behavior under test).
            var rolloverResult = ShellRunner.RunScript(
                "compute-rollover-diff.sh",
                new[] { "--root", fakeRepo },
                workingDir: fakeRepo);
            rolloverResult.ExitCode.Should().Be(0,
                because: $"rollover stderr: {rolloverResult.Stderr}");

            // Assertions on the aggregate artifacts.

            // 1. File tree contains the three ext-reflect outputs.
            var generatedFiles = Directory
                .EnumerateFiles(extOut, "*.md", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(extOut, p).Replace('\\', '/'))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            generatedFiles.Should().BeEquivalentTo(
                new[] { "commands.md", "configuration.md", "views.md" });

            // 2. Commands file lists the three fixture commands in stable order.
            var commandsMd = File.ReadAllText(Path.Combine(extOut, "commands.md"));
            commandsMd.Should().Contain("`ppds.fixture.alpha`");
            commandsMd.Should().Contain("`ppds.fixture.beta`");
            commandsMd.Should().Contain("`ppds.fixture.gamma`");

            // 3. PR body shape: header + Auth section with the new FreshType
            //    in Added and BaselineTwo in Removed.
            prBody.Should().Contain("# Public API surface changes");
            prBody.Should().Contain("Baseline: `v1.0.0`");
            prBody.Should().Contain("## PPDS.Auth");
            prBody.Should().Contain("`PPDS.Auth.FreshType`");
            prBody.Should().Contain("`PPDS.Auth.BaselineTwo`");
            prBody.Should().Contain("Removed (1):");

            // 4. Rollover cleared Unshipped back to the minimal form and
            //    Shipped still contains FreshType (unchanged because it was
            //    already there — idempotent).
            var unshippedAfter = File.ReadAllText(
                Path.Combine(authDir, "PublicAPI.Unshipped.txt"));
            unshippedAfter.Trim().Should().Be("#nullable enable");

            var shippedAfter = File.ReadAllText(
                Path.Combine(authDir, "PublicAPI.Shipped.txt"));
            shippedAfter.Should().Contain("PPDS.Auth.FreshType");
            shippedAfter.Should().Contain("PPDS.Auth.BaselineOne");
        }
        finally
        {
            ShellRunner.DeleteQuietly(fakeRepo);
        }
    }

    /// <summary>
    /// AC-26: rollover appends every Unshipped entry to Shipped, sorts +
    /// dedupes, and resets Unshipped across two fake library dirs.
    /// </summary>
    [Fact]
    public void RolloverMovesUnshippedEntries()
    {
        var fakeRepo = ShellRunner.MakeTempDir("rollover");
        try
        {
            // Populate two of the four library directories.
            var dataverseDir = Path.Combine(fakeRepo, "src", "PPDS.Dataverse");
            var authDir = Path.Combine(fakeRepo, "src", "PPDS.Auth");
            Directory.CreateDirectory(dataverseDir);
            Directory.CreateDirectory(authDir);

            File.WriteAllText(
                Path.Combine(dataverseDir, "PublicAPI.Shipped.txt"),
                "#nullable enable\nPPDS.Dataverse.AlreadyShipped\n");
            File.WriteAllText(
                Path.Combine(dataverseDir, "PublicAPI.Unshipped.txt"),
                "#nullable enable\n"
                + "PPDS.Dataverse.NewBeta\n"
                + "PPDS.Dataverse.NewAlpha\n"
                + "PPDS.Dataverse.NewGamma\n");

            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Shipped.txt"),
                "#nullable enable\n");
            File.WriteAllText(
                Path.Combine(authDir, "PublicAPI.Unshipped.txt"),
                "#nullable enable\n"
                + "PPDS.Auth.A\n"
                + "PPDS.Auth.B\n"
                + "PPDS.Auth.A\n"); // duplicate — must be deduped

            // The other two are intentionally missing; the script should
            // skip them with a "does not exist" message on stderr.

            var result = ShellRunner.RunScript(
                "compute-rollover-diff.sh",
                new[] { "--root", fakeRepo });
            result.ExitCode.Should().Be(0,
                because: $"stderr: {result.Stderr}");

            // Dataverse: Shipped gained all three new entries (sorted) and
            // kept its original entry. Unshipped reset.
            var dvShipped = File.ReadAllLines(
                Path.Combine(dataverseDir, "PublicAPI.Shipped.txt"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            dvShipped.Should().Contain("PPDS.Dataverse.AlreadyShipped");
            dvShipped.Should().Contain("PPDS.Dataverse.NewAlpha");
            dvShipped.Should().Contain("PPDS.Dataverse.NewBeta");
            dvShipped.Should().Contain("PPDS.Dataverse.NewGamma");

            // Sorted: each occurrence index is strictly increasing in the
            // LC_ALL=C (ordinal) order.
            var ordinal = string.Compare("PPDS.Dataverse.NewAlpha",
                "PPDS.Dataverse.NewBeta", StringComparison.Ordinal);
            ordinal.Should().BeLessThan(0);
            var alphaIdx = Array.IndexOf(dvShipped, "PPDS.Dataverse.NewAlpha");
            var betaIdx = Array.IndexOf(dvShipped, "PPDS.Dataverse.NewBeta");
            var gammaIdx = Array.IndexOf(dvShipped, "PPDS.Dataverse.NewGamma");
            alphaIdx.Should().BeLessThan(betaIdx);
            betaIdx.Should().BeLessThan(gammaIdx);

            var dvUnshipped = File.ReadAllText(
                Path.Combine(dataverseDir, "PublicAPI.Unshipped.txt"));
            dvUnshipped.Trim().Should().Be("#nullable enable");

            // Auth: duplicate collapsed.
            var authShipped = File.ReadAllLines(
                Path.Combine(authDir, "PublicAPI.Shipped.txt"))
                .Where(l => l == "PPDS.Auth.A")
                .ToArray();
            authShipped.Should().HaveCount(1, because: "sort -u must collapse duplicates");

            var authUnshipped = File.ReadAllText(
                Path.Combine(authDir, "PublicAPI.Unshipped.txt"));
            authUnshipped.Trim().Should().Be("#nullable enable");
        }
        finally
        {
            ShellRunner.DeleteQuietly(fakeRepo);
        }
    }

    /// <summary>
    /// AC-27: when a prior baseline-rollover PR is still open, the release
    /// workflow aborts with a clear message. We simulate this by placing a
    /// <c>gh</c> shim on <c>PATH</c> that returns a canned matching PR.
    /// </summary>
    [Fact]
    public void AbortsOnOpenPriorRolloverPr()
    {
        var shimDir = ShellRunner.MakeTempDir("ghshim");
        try
        {
            // Emulate `gh api --jq` by returning the already-filtered lines.
            // The check script's expected jq filter prints
            //   "#<number> <url>"
            // per matching PR; our shim prints that directly, ignoring args.
            var shimName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "gh.cmd"
                : "gh";
            var shimPath = Path.Combine(shimDir, shimName);

            // On Windows we invoke gh via cmd — the check script uses the GH
            // env var as the literal binary name. Plain bash-executable
            // script works via Git-for-Windows bash on Windows too.
            var shimBody = """
#!/usr/bin/env bash
echo "#42 https://github.com/example/ppds/pulls/42 — chore(release): v1.2.0 baseline rollover"
exit 0
""";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // A bash-script-style shim works because the check script
                // invokes $GH_BIN directly and we point GH at this file;
                // bash on Windows interprets the shebang and runs the body.
                shimPath = Path.Combine(shimDir, "gh");
            }

            File.WriteAllText(shimPath, shimBody.ReplaceLineEndings("\n"));
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // chmod +x equivalent via File.SetUnixFileMode on .NET 7+.
                File.SetUnixFileMode(shimPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var env = new Dictionary<string, string?>
            {
                ["GH"] = shimPath,
                ["GITHUB_REPOSITORY"] = "example/ppds",
            };

            var result = ShellRunner.RunScript(
                "check-open-rollover.sh",
                Array.Empty<string>(),
                env: env);

            result.ExitCode.Should().NotBe(0,
                because: "an open prior rollover PR must abort the release");

            // The script writes all diagnostics to stderr, including the
            // canonical error text.
            result.Stderr.Should().Contain("baseline rollover");
            result.Stderr.Should().Contain(
                "Open prior rollover PR must be merged or closed before new tag");
            result.Stderr.Should().Contain("#42");
        }
        finally
        {
            ShellRunner.DeleteQuietly(shimDir);
        }
    }
}
