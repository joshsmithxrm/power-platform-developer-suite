using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Auth;

/// <summary>
/// Structural regression tests for shakedown finding L6 (2026-04-20):
/// <c>auth who</c> identity header must go to stderr, not stdout.
/// </summary>
/// <remarks>
/// PPDS NEVER rule I1: CLI stdout is reserved for data only.
/// Status messages (like "Connected as username") must use <see cref="Console.Error.WriteLine"/>.
/// This ensures <c>ppds auth who | jq .</c> and similar pipes work correctly.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthWhoStderrTests
{
    /// <summary>
    /// The "Connected as {identity}" line in AuthCommandGroup must use
    /// <c>Console.Error.WriteLine</c>, not <c>Console.WriteLine</c>.
    /// Verified by reading the source file to guard the call-site directly.
    /// </summary>
    [Fact]
    public void AuthWho_IdentityHeader_WritesToStderr_NotStdout()
    {
        var srcPath = GetAuthCommandGroupSrcPath();
        if (srcPath == null)
        {
            // Packaged / CI form without source — skip
            return;
        }

        var source = File.ReadAllText(srcPath);

        // Find the "Connected as" line
        var lines = source.Split('\n');
        var connectedLine = lines.FirstOrDefault(l => l.Contains("Connected as {identity}"));

        connectedLine.Should().NotBeNull(
            because: "AuthCommandGroup must contain a 'Connected as {identity}' status line");

        connectedLine!.Should().Contain(
            "Console.Error.WriteLine",
            because: "the identity header 'Connected as ...' is a status message that must go to stderr " +
                     "(I1 violation fix, shakedown finding L6 — stdout is reserved for data only)");

        connectedLine.Should().NotContain(
            "Console.WriteLine(",
            because: "Console.WriteLine without .Error writes to stdout, which breaks piping e.g. 'ppds auth who | jq .'");
    }

    /// <summary>
    /// The blank separator line after "Connected as {identity}" must also use stderr.
    /// </summary>
    [Fact]
    public void AuthWho_BlankLineSeparator_WritesToStderr_NotStdout()
    {
        var srcPath = GetAuthCommandGroupSrcPath();
        if (srcPath == null) return;

        var source = File.ReadAllText(srcPath);
        var lines = source.Split('\n');

        // Find the index of the "Connected as" line
        var connectedIdx = Array.FindIndex(lines, l => l.Contains("Connected as {identity}"));
        connectedIdx.Should().BeGreaterThan(0, because: "Connected as line must exist");

        // The very next non-empty meaningful line should be the blank-line separator
        // (directly after the identity line)
        var nextLine = lines[connectedIdx + 1];

        nextLine.Should().Contain(
            "Console.Error.WriteLine()",
            because: "the blank separator after the identity header must also go to stderr (L6 fix)");
    }

    private static string? GetAuthCommandGroupSrcPath()
    {
        var assembly = typeof(AuthWhoStderrTests).Assembly;
        var location = Path.GetDirectoryName(assembly.Location)!;
        // Navigate from test output dir to source tree
        var candidate = Path.GetFullPath(
            Path.Combine(location, "..", "..", "..", "..", "..", "src", "PPDS.Cli", "Commands", "Auth", "AuthCommandGroup.cs"));
        return File.Exists(candidate) ? candidate : null;
    }
}
