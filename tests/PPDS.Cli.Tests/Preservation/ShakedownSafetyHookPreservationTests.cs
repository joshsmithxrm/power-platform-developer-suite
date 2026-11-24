namespace PPDS.Cli.Tests.Preservation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

/// <summary>
/// Guards the shakedown write-block in <c>.claude/hooks/shakedown-safety.py</c>
/// so it cannot be weakened without a .NET test noticing. The hook decides by
/// command path: its <c>_CLI_COMMANDS</c> table classes every CLI command path,
/// and during a shakedown only the read classes pass. These tests read that
/// table as text (the hook is not run from .NET) and pin that every command
/// path whose last word is a write verb is not classed as read, that
/// <c>plugins deploy</c> keeps its dry-run condition, that read-only paths stay
/// read, and that the hook still prints its write-block tag.
/// </summary>
public class ShakedownSafetyHookPreservationTests
{
    // The verbs the write-block has always refused; a command path ending in
    // one of them writes.
    private static readonly string[] WriteVerbs =
    {
        "create", "update", "delete", "remove", "import", "apply",
        "register", "unregister", "publish", "truncate", "drop",
        "reset", "set",
    };


    private static string HookText()
    {
        var hookPath = Path.Combine(PathHelpers.RepoRoot(), ".claude", "hooks", "shakedown-safety.py");
        Assert.True(File.Exists(hookPath), $"Hook not found: {hookPath}");
        return File.ReadAllText(hookPath);
    }

    /// <summary>
    /// The <c>_CLI_COMMANDS</c> table: one line per parent path ("." is the
    /// root), each child as <c>name=class</c>.
    /// </summary>
    private static Dictionary<string, string> CommandTable()
    {
        var match = Regex.Match(HookText(), "^_CLI_COMMANDS = \"\"\"(.*?)\"\"\"", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, "_CLI_COMMANDS table not found in the hook");
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in match.Groups[1].Value.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            Assert.True(colon > 0, $"Malformed table line: {line}");
            var parent = line[..colon].Trim();
            var prefix = parent == "." ? "" : parent + " ";
            foreach (var item in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split('=');
                Assert.Equal(2, parts.Length);
                table[prefix + parts[0]] = parts[1];
            }
        }

        return table;
    }

    [Fact]
    public void BlockTag_IsPresent()
    {
        Assert.Contains("BLOCKED [shakedown-safety/readonly]", HookText());
    }

    [Fact]
    public void CommandPathsEndingInAWriteVerb_AreNotClassedAsRead()
    {
        var table = CommandTable();
        var writes = table.Where(kv => WriteVerbs.Contains(kv.Key.Split(' ')[^1])).ToList();
        Assert.True(writes.Count >= 50, $"Only {writes.Count} write-verb paths in the table");
        // "w" is refused; "g" (a group such as `plugins register`) passes only
        // alone, where it prints help, and none of its children may read.
        var weak = writes.Where(kv => kv.Value != "w" && kv.Value != "g").Select(kv => kv.Key).ToList();
        Assert.True(weak.Count == 0, "Write paths not classed as writes: " + string.Join(", ", weak));
        foreach (var group in writes.Where(kv => kv.Value == "g").Select(kv => kv.Key))
        {
            var children = table.Where(kv => kv.Key.StartsWith(group + " ", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(children);
            Assert.All(children, kv => Assert.Equal("w", kv.Value));
        }
        Assert.Contains(writes, kv => kv.Key == "data delete" && kv.Value == "w");
        Assert.Contains(writes, kv => kv.Key == "solutions import" && kv.Value == "w");
    }

    [Fact]
    public void PluginsDeploy_KeepsItsDryRunCondition()
    {
        Assert.Equal("dry-run", CommandTable()["plugins deploy"]);
    }

    [Theory]
    [InlineData("env list")]
    [InlineData("env who")]
    [InlineData("data export")]
    [InlineData("solutions list")]
    [InlineData("query fetch")]
    public void ReadOnlyPaths_StayRead(string path)
    {
        Assert.Equal("r", CommandTable()[path]);
    }
}
