using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// The check that no unit test can start an interactive sign-in (#1757): tests/Directory.Build.props
/// turns the guard's switch on for every unit test project, and every interactive token acquisition
/// in the product calls the guard first.
/// </summary>
public class InteractiveSignInGuardTests
{
    private static readonly Regex InteractiveAcquisition = new(
        @"\.(AcquireTokenInteractive|AcquireTokenWithDeviceCode)\s*\(", RegexOptions.Compiled);

    private static readonly Regex AzureIdentityInteractiveCredential = new(
        @"\b(InteractiveBrowserCredential|DeviceCodeCredential)\s*\(", RegexOptions.Compiled);

    // How far above an acquisition the guard call may sit (the flow's log and output lines sit between).
    private const int GuardWindowLines = 12;

    [Fact]
    public void UnitTestRun_HasInteractiveSignInBlocked()
    {
        InteractiveSignInGuard.IsBlocked.Should().BeTrue(
            because: $"tests/Directory.Build.props sets '{InteractiveSignInGuard.SwitchName}' for unit test projects");
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("device code")]
    public void ThrowIfBlocked_FailsAtOnce_NamingTheFlowAndTheSwitch(string flow)
    {
        var act = () => InteractiveSignInGuard.ThrowIfBlocked(flow);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"({flow})").And.Contain(InteractiveSignInGuard.SwitchName);
    }

    [Fact]
    public void EveryInteractiveAcquisitionInTheProduct_CallsTheGuardFirst()
    {
        var offenders = new List<string>();
        var acquisitions = 0;

        foreach (var path in ProductSourceFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!InteractiveAcquisition.IsMatch(lines[i]))
                {
                    continue;
                }

                acquisitions++;
                var windowStart = Math.Max(0, i - GuardWindowLines);
                var guarded = lines[windowStart..i].Any(l => l.Contains("InteractiveSignInGuard.ThrowIfBlocked("));
                if (!guarded)
                {
                    offenders.Add($"{Relative(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        acquisitions.Should().BeGreaterThan(0, because: "the scan must find the product's interactive flows");
        offenders.Should().BeEmpty(
            because: "an interactive acquisition without the guard can open a browser or wait on a device code inside a unit test");
    }

    [Fact]
    public void TheProductUsesNoAzureIdentityInteractiveCredential()
    {
        // The guard covers MSAL's interactive flows only; an Azure.Identity interactive credential
        // would bypass it and needs its own guard call before this test is changed.
        var uses = ProductSourceFiles()
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (line, index))
                .Where(x => AzureIdentityInteractiveCredential.IsMatch(x.line)
                    && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(x => $"{Relative(path)}:{x.index + 1}: {x.line.Trim()}"))
            .ToList();

        uses.Should().BeEmpty();
    }

    private static string SourceRoot
    {
        get
        {
            var baseDir = Path.GetDirectoryName(typeof(InteractiveSignInGuardTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src"));
        }
    }

    private static IEnumerable<string> ProductSourceFiles()
    {
        Directory.Exists(SourceRoot).Should().BeTrue(because: $"the scan reads the product sources under {SourceRoot}");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{separator}obj{separator}") && !p.Contains($"{separator}bin{separator}")
                && !p.Contains($"{separator}node_modules{separator}"));
    }

    private static string Relative(string path) => Path.GetRelativePath(SourceRoot, path);
}
