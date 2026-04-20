using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Tests.Infrastructure.Safety.Fakes;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Safety;

/// <summary>
/// AC-02 through AC-13 for <see cref="ShakedownGuard"/>. See the spec at
/// <c>specs/shakedown-guard.md</c> for the rationale behind each case.
/// </summary>
public class ShakedownGuardTests
{
    private const string SentinelRelPath = ".claude/state/shakedown-active.json";

    private sealed record Harness(
        FakeEnvironment Env,
        FakeFileSystem Fs,
        FakeClock Clock,
        FakeLogger<ShakedownGuard> Log,
        ShakedownGuard Guard,
        string ProjectRoot,
        string SentinelAbsolutePath);

    private static Harness CreateHarness(string? projectDir = null, bool registerProjectDir = true)
    {
        var root = projectDir ?? Path.Combine(Path.GetTempPath(), "ppds-shakedown-tests-" + Guid.NewGuid().ToString("N"));
        var env = new FakeEnvironment();
        var fs = new FakeFileSystem();

        if (registerProjectDir)
        {
            fs.AddDirectory(root);
            env["CLAUDE_PROJECT_DIR"] = root;
        }

        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero) };
        var log = new FakeLogger<ShakedownGuard>();
        var guard = new ShakedownGuard(env, fs, clock, log);

        var absPath = Path.Combine(root, SentinelRelPath.Replace('/', Path.DirectorySeparatorChar));
        return new Harness(env, fs, clock, log, guard, root, absPath);
    }

    private static string BuildSentinelJson(DateTimeOffset startedAt, string scope = "test")
        => $"{{\"started_at\":\"{startedAt:O}\",\"scope\":\"{scope}\"}}";

    // ---------------------------------------------------------------------
    // AC-02
    // ---------------------------------------------------------------------
    [Fact]
    public void Block_When_EnvVarEqualsOne()
    {
        var h = CreateHarness();
        h.Env["PPDS_SHAKEDOWN"] = "1";

        var ex = Assert.Throws<PpdsException>(() => h.Guard.EnsureCanMutate("plugintraces.delete"));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
        Assert.Contains("env:PPDS_SHAKEDOWN", ex.UserMessage);
    }

    // ---------------------------------------------------------------------
    // AC-03
    // ---------------------------------------------------------------------
    [Fact]
    public void Block_When_FreshSentinelPresent()
    {
        var h = CreateHarness();
        var startedAt = h.Clock.UtcNow.AddHours(-1);
        h.Fs.AddFile(h.SentinelAbsolutePath, BuildSentinelJson(startedAt));

        var ex = Assert.Throws<PpdsException>(() => h.Guard.EnsureCanMutate("solutions.import"));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
        Assert.Contains("sentinel:" + SentinelRelPath, ex.UserMessage);
    }

    // ---------------------------------------------------------------------
    // AC-04
    // ---------------------------------------------------------------------
    [Fact]
    public void Allow_When_StaleSentinel_NoSideEffects()
    {
        var h = CreateHarness();
        var startedAt = h.Clock.UtcNow.AddHours(-25);
        h.Fs.AddFile(h.SentinelAbsolutePath, BuildSentinelJson(startedAt));

        // Should NOT throw.
        h.Guard.EnsureCanMutate("webresources.publish");

        // Sentinel file must still exist (no deletion).
        Assert.True(h.Fs.FileExists(h.SentinelAbsolutePath));

        // No warnings should have been emitted for the legitimate stale case.
        Assert.Equal(0, h.Log.WarningCount);
    }

    // ---------------------------------------------------------------------
    // AC-05
    // ---------------------------------------------------------------------
    [Fact]
    public void Allow_When_NoSignalsPresent()
    {
        var h = CreateHarness();
        // No env var, no sentinel.
        h.Guard.EnsureCanMutate("roles.update");
        Assert.Equal(0, h.Log.WarningCount);
    }

    // ---------------------------------------------------------------------
    // AC-06
    // ---------------------------------------------------------------------
    [Fact]
    public void Allow_And_Warn_When_SentinelCorrupt()
    {
        var h = CreateHarness();
        h.Fs.AddFile(h.SentinelAbsolutePath, "not json {");

        // Fail-open: should not throw.
        h.Guard.EnsureCanMutate("envvars.update");

        Assert.Equal(1, h.Log.WarningCount);
    }

    // ---------------------------------------------------------------------
    // AC-07
    // ---------------------------------------------------------------------
    public enum ProjectRootScenario
    {
        EnvUnset,
        EnvSetExists,
        EnvSetMissing,
        EnvEmpty,
    }

    [Theory]
    [InlineData(ProjectRootScenario.EnvUnset)]
    [InlineData(ProjectRootScenario.EnvSetExists)]
    [InlineData(ProjectRootScenario.EnvSetMissing)]
    [InlineData(ProjectRootScenario.EnvEmpty)]
    public void ProjectRoot_ResolvesInExpectedOrder(ProjectRootScenario scenario)
    {
        // Use a sandbox CWD so the test cannot be influenced by whatever the
        // real CWD happens to be and so the CWD-fallback cases never spuriously
        // find a real sentinel. The harness's CWD is the sandbox for each case.
        var sandboxCwd = Path.Combine(Path.GetTempPath(), "ppds-shakedown-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxCwd);
        var originalCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(sandboxCwd);

            var env = new FakeEnvironment();
            var fs = new FakeFileSystem();
            var clock = new FakeClock();
            var log = new FakeLogger<ShakedownGuard>();

            string? claudeProjectDir = null;
            string? stalePath = null;

            switch (scenario)
            {
                case ProjectRootScenario.EnvUnset:
                    // Do not set CLAUDE_PROJECT_DIR — should fall back to CWD.
                    break;
                case ProjectRootScenario.EnvSetExists:
                    claudeProjectDir = Path.Combine(Path.GetTempPath(), "ppds-pd-" + Guid.NewGuid().ToString("N"));
                    fs.AddDirectory(claudeProjectDir);
                    env["CLAUDE_PROJECT_DIR"] = claudeProjectDir;
                    break;
                case ProjectRootScenario.EnvSetMissing:
                    stalePath = Path.Combine(Path.GetTempPath(), "ppds-stale-" + Guid.NewGuid().ToString("N"));
                    env["CLAUDE_PROJECT_DIR"] = stalePath;
                    // Directory intentionally NOT added.
                    break;
                case ProjectRootScenario.EnvEmpty:
                    env["CLAUDE_PROJECT_DIR"] = string.Empty;
                    break;
            }

            var guard = new ShakedownGuard(env, fs, clock, log);

            // Put an env-var signal so we can detect "guard ran" without
            // having to emulate a sentinel at the resolved root.
            env["PPDS_SHAKEDOWN"] = "1";
            var ex = Assert.Throws<PpdsException>(() => guard.EnsureCanMutate("sentinel.probe"));
            Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);

            // Reset env var and re-instantiate a guard so we can examine the
            // sentinel path resolution warning behaviour cleanly.
            env["PPDS_SHAKEDOWN"] = null;
            var freshLog = new FakeLogger<ShakedownGuard>();
            var freshGuard = new ShakedownGuard(env, fs, clock, freshLog);
            freshGuard.EnsureCanMutate("sentinel.probe");

            switch (scenario)
            {
                case ProjectRootScenario.EnvUnset:
                case ProjectRootScenario.EnvEmpty:
                case ProjectRootScenario.EnvSetExists:
                    Assert.Equal(0, freshLog.WarningCount);
                    break;
                case ProjectRootScenario.EnvSetMissing:
                    Assert.Equal(1, freshLog.WarningCount);
                    Assert.Contains(stalePath!, freshLog.Entries[0].Message);
                    break;
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            try { Directory.Delete(sandboxCwd, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ---------------------------------------------------------------------
    // AC-09
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData("env")]
    [InlineData("sentinel")]
    public void ExceptionShape_MatchesSpec(string driver)
    {
        var h = CreateHarness();
        const string op = "plugintraces.deleteOlderThan";

        if (driver == "env")
        {
            h.Env["PPDS_SHAKEDOWN"] = "1";
        }
        else
        {
            h.Fs.AddFile(h.SentinelAbsolutePath, BuildSentinelJson(h.Clock.UtcNow.AddMinutes(-30)));
        }

        var ex = Assert.Throws<PpdsException>(() => h.Guard.EnsureCanMutate(op));

        Assert.Contains(op, ex.UserMessage);
        Assert.Contains("Bypass", ex.UserMessage);

        Assert.NotNull(ex.Context);
        Assert.Equal(op, ex.Context!["operation"]);

        var source = (string)ex.Context["activationSource"];
        if (driver == "env")
        {
            Assert.Equal("env:PPDS_SHAKEDOWN", source);
            Assert.False(ex.Context.ContainsKey("sentinelPath"));
            Assert.False(ex.Context.ContainsKey("sentinelAgeSeconds"));
        }
        else
        {
            Assert.StartsWith("sentinel:", source);
            Assert.Equal(SentinelRelPath, ex.Context["sentinelPath"]);
            var age = Assert.IsType<double>(ex.Context["sentinelAgeSeconds"]);
            Assert.True(age > 0);
        }

        Assert.Contains(source, ex.UserMessage);
    }

    // ---------------------------------------------------------------------
    // AC-10
    // ---------------------------------------------------------------------
    [Fact]
    public void Concurrent_Calls_AreConsistent()
    {
        var h = CreateHarness();
        h.Env["PPDS_SHAKEDOWN"] = "1";

        var throwCount = 0;
        var otherErrorCount = 0;

        Parallel.For(
            0,
            1000,
            new ParallelOptions { MaxDegreeOfParallelism = 50 },
            _ =>
            {
                try
                {
                    h.Guard.EnsureCanMutate("concurrent.test");
                }
                catch (PpdsException)
                {
                    Interlocked.Increment(ref throwCount);
                }
                catch
                {
                    Interlocked.Increment(ref otherErrorCount);
                }
            });

        Assert.Equal(1000, throwCount);
        Assert.Equal(0, otherErrorCount);
    }

    // ---------------------------------------------------------------------
    // AC-11
    // ---------------------------------------------------------------------
    [Fact]
    public void Cache_TTL_SuppressesRepeatedFileStats()
    {
        var h = CreateHarness();
        // Inactive case — sentinel absent. We care about stat suppression.
        for (int i = 0; i < 10; i++)
            h.Guard.EnsureCanMutate("cache.probe");

        // First call did a FileExists; follow-ups should hit the cache.
        Assert.True(h.Fs.StatCount <= 1, $"Expected <=1 stat within TTL, got {h.Fs.StatCount}");

        // Advance beyond TTL — next call must re-stat.
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Guard.EnsureCanMutate("cache.probe");
        Assert.True(h.Fs.StatCount >= 2, $"Expected stat-count to increment after TTL, got {h.Fs.StatCount}");
    }

    // ---------------------------------------------------------------------
    // AC-12
    // ---------------------------------------------------------------------
    [Fact]
    public void Allow_And_Warn_When_SentinelLockedForWrite()
    {
        var h = CreateHarness();
        // File "exists" (FileExists returns true) but OpenRead raises IOException.
        h.Fs.AddFile(h.SentinelAbsolutePath, "ignored");
        h.Fs.ThrowOnOpen(h.SentinelAbsolutePath, new IOException("file is locked"));

        // Fail-open: no throw.
        h.Guard.EnsureCanMutate("locked.sentinel");

        Assert.Equal(1, h.Log.WarningCount);
    }

    // ---------------------------------------------------------------------
    // AC-13
    // ---------------------------------------------------------------------
    [Theory]
    // Truthy-but-not-"1" values → inactive AND warn.
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    // Other non-"1" values → inactive AND silent.
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("2", false)]
    [InlineData("garbage", false)]
    public void Warn_When_EnvVar_IsTruthyNonOne(string value, bool expectWarning)
    {
        var h = CreateHarness();
        h.Env["PPDS_SHAKEDOWN"] = value;

        // Guard must NOT throw — only "1" activates.
        h.Guard.EnsureCanMutate("truthy.probe");

        if (expectWarning)
        {
            Assert.Equal(1, h.Log.WarningCount);
            Assert.Contains(value, h.Log.Entries.Single(e => e.Level == LogLevel.Warning).Message);
        }
        else
        {
            Assert.Equal(0, h.Log.WarningCount);
        }
    }
}
