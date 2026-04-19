using FluentAssertions;
using Xunit;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Regression coverage for issue #794 — <see cref="ShellRunner.RunGit"/> must
/// not let inherited <c>GIT_DIR</c> / <c>GIT_INDEX_FILE</c> env vars reach the
/// child <c>git</c> process, or commits against the temp fixture repo will
/// silently land in the invoking repo.
/// </summary>
public sealed class ShellRunnerTests
{
    /// <summary>
    /// Simulates the pre-commit-hook environment (parent sets GIT_DIR /
    /// GIT_INDEX_FILE pointing at an "invoking" repo), runs the same init +
    /// add + commit sequence the workflow tests use against a separate
    /// "fake" repo, and asserts that the invoking repo's HEAD is unchanged.
    /// </summary>
    [Fact]
    public void RunGit_DoesNotLeakCommitsIntoInvokingRepo_WhenGitDirInherited()
    {
        // ---- 1. Stand up the "invoking" (sandbox) repo. ------------------
        var sandbox = ShellRunner.MakeTempDir("sandbox-invoking");

        // Save whatever GIT_* vars the harness already has so we can restore
        // them after the test (belt-and-braces — even though xunit runs each
        // test in the same AppDomain, later tests should see the pristine env).
        var originalGitDir = Environment.GetEnvironmentVariable("GIT_DIR");
        var originalGitIndexFile = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        var originalGitWorkTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");

        string? sandboxHeadBefore = null;
        var fakeRepo = ShellRunner.MakeTempDir("fake-target");

        try
        {
            // Initialize the sandbox and give it a seed commit so HEAD has a
            // stable value to compare against later.
            ShellRunner.RunGit(sandbox, "init", "--initial-branch=main").ExitCode.Should().Be(0);
            File.WriteAllText(Path.Combine(sandbox, "seed.txt"), "seed\n");
            ShellRunner.RunGit(sandbox, "add", "seed.txt").ExitCode.Should().Be(0);
            ShellRunner.RunGit(sandbox, "commit", "-m", "seed").ExitCode.Should().Be(0);

            sandboxHeadBefore = ShellRunner.RunGit(sandbox, "rev-parse", "HEAD").Stdout.Trim();
            sandboxHeadBefore.Should().NotBeNullOrWhiteSpace();

            // ---- 2. Set the inherited-GIT_DIR trap. ----------------------
            // Point the parent process's discovery vars at the sandbox repo's
            // .git — this is what the pre-commit hook's child process sees
            // when a commit is in progress in a real repo.
            var sandboxGitDir = Path.Combine(sandbox, ".git");
            Environment.SetEnvironmentVariable("GIT_DIR", sandboxGitDir);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(sandboxGitDir, "index"));

            // ---- 3. Run the fake-target sequence via ShellRunner. --------
            // This mirrors DocsReleaseWorkflowTests' fixture-repo setup.
            ShellRunner.RunGit(fakeRepo, "init", "--initial-branch=main").ExitCode.Should().Be(0);
            File.WriteAllText(Path.Combine(fakeRepo, "fake.txt"), "fake\n");
            ShellRunner.RunGit(fakeRepo, "add", "-A").ExitCode.Should().Be(0);
            ShellRunner.RunGit(fakeRepo, "commit", "-m", "baseline").ExitCode.Should().Be(0);
        }
        finally
        {
            // Restore env BEFORE we verify HEAD — otherwise our own rev-parse
            // would also walk through the trap and confuse the diagnosis.
            Environment.SetEnvironmentVariable("GIT_DIR", originalGitDir);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", originalGitIndexFile);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", originalGitWorkTree);
        }

        try
        {
            // ---- 4. Verify the sandbox HEAD did not move. ----------------
            var sandboxHeadAfter = ShellRunner.RunGit(sandbox, "rev-parse", "HEAD").Stdout.Trim();
            sandboxHeadAfter.Should().Be(
                sandboxHeadBefore,
                because: "the fake-target commit must not leak into the invoking repo via inherited GIT_DIR (#794)");
        }
        finally
        {
            ShellRunner.DeleteQuietly(sandbox);
            ShellRunner.DeleteQuietly(fakeRepo);
        }
    }
}
