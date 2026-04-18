using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Cross-generator validation that every generated reference file parses
/// cleanly under strict MDX. Backs AC-23 — the product contract is that
/// whatever the four generators emit can be dropped into ppds-docs without
/// authoring a single character by hand.
/// </summary>
public class MdxParseTests
{
    private readonly ITestOutputHelper _output;

    public MdxParseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StrictMdxAcceptsGeneratedReference()
    {
        var repoRoot = RepoRoot.Find();
        var fixtureRoots = WorkflowFixtures.ExpectedRoots(repoRoot);
        fixtureRoots.Should().NotBeEmpty(
            "at least one generator's Fixtures/Expected directory must exist — without them there is nothing to validate");

        // Stage every generator fixture .md file plus a hand-crafted edge-case
        // file into a temp dir. The staging step is purely to give parse.mjs a
        // stable flat list of paths; we never mutate the source files.
        var temp = Path.Combine(Path.GetTempPath(), "ppds-docsgen-mdx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var staged = new List<string>();
        try
        {
            foreach (var root in fixtureRoots)
            {
                foreach (var md in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
                {
                    // Flatten with a path-tagged name so collisions across the
                    // four generators don't clobber each other in temp.
                    var relative = Path.GetRelativePath(repoRoot, md).Replace('\\', '_').Replace('/', '_');
                    var staged_path = Path.Combine(temp, relative);
                    File.Copy(md, staged_path);
                    staged.Add(staged_path);
                }
            }

            staged.Should().NotBeEmpty("at least one generated .md fixture must be present");

            // Hand-crafted edge cases — exercises the specific MDX-unsafe
            // patterns called out in AC-23 plus a round-trip of the entity
            // form the generator emits when a `<` escaped into prose.
            var edgeCases = Path.Combine(temp, "edge-cases.md");
            File.WriteAllText(edgeCases, EdgeCaseFixture, Encoding.UTF8);
            staged.Add(edgeCases);

            // Install @mdx-js/mdx on demand. If install fails (offline CI,
            // no registry), Skip rather than fail — the spec explicitly
            // permits this, and the compiler is validated in its own test
            // matrix. The terminology test still runs in pure C#.
            var fixtureDir = Path.Combine(AppContext.BaseDirectory, "mdx-fixture");
            Directory.Exists(fixtureDir).Should().BeTrue(
                "mdx-fixture/{package.json,parse.mjs} must be copied to bin by the csproj");

            if (!TryInstallNodeModules(fixtureDir, out var installError))
            {
                // Spec allows skipping AC-23 when the Node helper can't
                // resolve dependencies (offline CI, no registry). Without
                // Xunit.SkippableFact (constraint: no extra packages) the
                // closest equivalent is an early return that records the
                // reason in the test log — the caller sees the notice and
                // the test stays green instead of failing for the wrong
                // reason.
                _output.WriteLine($"SKIP AC-23: npm install failed in {fixtureDir} — {installError}");
                return;
            }

            var (exit, stdout, stderr) = RunNode(
                fixtureDir,
                new[] { Path.Combine(fixtureDir, "parse.mjs") }
                    .Concat(staged)
                    .ToArray());

            exit.Should().Be(0,
                "strict MDX parse of every generated fixture must succeed. stderr:\n{0}\nstdout:\n{1}",
                stderr,
                stdout);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* best effort — tests running in parallel may race on cleanup */ }
        }
    }

    /// <summary>
    /// Hand-crafted content covering the tricky cases enumerated in the
    /// Phase 2.6 brief. If any one of these fails to parse under strict MDX,
    /// the corresponding generator path is unsafe and must be fixed.
    /// </summary>
    private const string EdgeCaseFixture = """
        <!-- Auto-generated fixture for strict MDX edge cases. -->

        # Edge cases

        Uses Task&lt;T&gt; heavily in prose, alongside `List<int>` inline.

        The signature `Dictionary<string, List<Account>>` appears both inline
        and in prose as Dictionary&lt;string, List&lt;Account&gt;&gt;.

        Nested backticks: ``foo `bar` baz`` should remain a single inline code
        span without tripping the parser.

        Round-trip HTML entities: raw &lt;T&gt; written as escaped entities
        is what `MdxEscape.Prose` emits and must survive the MDX compiler.

        ```csharp
        // Fenced code may contain anything, including Task<T> and Dictionary<K, V>.
        var d = new Dictionary<string, List<int>>();
        ```
        """;

    private static bool TryInstallNodeModules(string fixtureDir, out string error)
    {
        error = string.Empty;
        var nodeModules = Path.Combine(fixtureDir, "node_modules");
        if (Directory.Exists(nodeModules))
        {
            return true;
        }

        try
        {
            // `npm install --silent` — keeps output quiet so test logs stay
            // readable on red. We intentionally don't pin a lockfile: first
            // run resolves against the public registry, subsequent runs are
            // cached by the node_modules existence check.
            //
            // On Windows we route through `cmd /c` because npm is a .cmd
            // wrapper and ProcessStartInfo's CreateProcess path doesn't
            // expand the wrapper's `%~dp0` reliably — which causes npm to
            // resolve its own module against the fixture's (empty) CWD.
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm install --silent --no-audit --no-fund",
                    WorkingDirectory = fixtureDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "npm",
                    WorkingDirectory = fixtureDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("--silent");
                psi.ArgumentList.Add("--no-audit");
                psi.ArgumentList.Add("--no-fund");
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start npm");
            if (!proc.WaitForExit(milliseconds: 180_000))
            {
                proc.Kill(entireProcessTree: true);
                error = "npm install timed out after 180s";
                return false;
            }

            if (proc.ExitCode != 0)
            {
                error = $"npm install exit {proc.ExitCode}: {proc.StandardError.ReadToEnd()}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static (int exit, string stdout, string stderr) RunNode(string workingDir, string[] args)
    {
        // `node` ships as node.exe on Windows — a real executable, so
        // ProcessStartInfo + ArgumentList handles it without a shell. We
        // still fall back to the extension-less name on non-Windows.
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start node");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
