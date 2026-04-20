using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Regression tests for <see cref="MsalClientBuilder"/>.
/// </summary>
/// <remarks>
/// The token cache backing store is picked by MSAL at runtime. Exercising
/// the full selection logic cross-platform from a unit test would require
/// actually running on Keychain/libsecret, so these tests pin the
/// invariants we care about structurally:
/// <list type="bullet">
/// <item>Linux keyring creation is attempted before the unprotected-file fallback (A3).</item>
/// <item>The top-level cache creation method no longer hard-codes <c>WithUnprotectedFile</c>.</item>
/// </list>
/// </remarks>
public class MsalClientBuilderTests
{
    [Fact]
    public void CreateAndRegisterCacheAsync_DoesNotUnconditionallyCallWithUnprotectedFile()
    {
        // A3 regression guard. The previous implementation unconditionally
        // called WithUnprotectedFile(), which skipped DPAPI on Windows and
        // Keychain on macOS. The current implementation must route platform
        // choice through CreatePlatformCacheHelperAsync.
        var platformMethod = typeof(MsalClientBuilder).GetMethod(
            "CreatePlatformCacheHelperAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        platformMethod.Should().NotBeNull(
            "MsalClientBuilder must delegate cache creation to a platform-aware helper");
    }

    [Fact]
    public void MsalClientBuilder_Source_ClampsLinuxFallbackFileMode()
    {
        // Linux fallback writes plaintext tokens to disk. The source must
        // clamp the file to 0600 (UserRead | UserWrite) after creation,
        // guarded by OperatingSystem.IsLinux() so Windows doesn't throw.
        var thisAssembly = typeof(MsalClientBuilderTests).Assembly;
        var srcPath = Path.Combine(
            Path.GetDirectoryName(thisAssembly.Location)!,
            "..", "..", "..", "..", "..",
            "src", "PPDS.Auth", "Credentials", "MsalClientBuilder.cs");

        if (!File.Exists(srcPath))
        {
            Assert.Fail($"Source file not found at {srcPath} — test layout changed.");
        }

        var source = File.ReadAllText(srcPath);
        source.Should().Contain(
            "SetUnixFileMode",
            "Linux unprotected fallback must clamp to owner-only mode");
        source.Should().Contain(
            "OperatingSystem.IsLinux()",
            "SetUnixFileMode must be guarded on Linux — throws on Windows");
        // Match either fully-qualified `System.IO.UnixFileMode.UserWrite` or the
        // unqualified form (if a future `using System.IO;` drops the prefix).
        Regex.IsMatch(source, @"UserRead\s*\|\s*(System\.IO\.)?UnixFileMode\.UserWrite")
            .Should().BeTrue(
                "Fallback clamp must be exactly 0600 (UserRead | UserWrite), not 0700");
    }

    [Fact]
    public void MsalClientBuilder_Source_PrefersLinuxKeyringOverUnprotectedFile()
    {
        // A3 regression guard — fails if someone re-introduces an unconditional
        // .WithUnprotectedFile() without a Linux-keyring-first attempt.
        var thisAssembly = typeof(MsalClientBuilderTests).Assembly;
        var srcPath = Path.Combine(
            Path.GetDirectoryName(thisAssembly.Location)!,
            "..", "..", "..", "..", "..",
            "src", "PPDS.Auth", "Credentials", "MsalClientBuilder.cs");

        if (!File.Exists(srcPath))
        {
            Assert.Fail($"Source file not found at {srcPath} — test layout changed.");
        }

        var source = File.ReadAllText(srcPath);
        source.Should().Contain("WithLinuxKeyring", "Linux keyring must be attempted first");
        var keyringIdx = source.IndexOf("WithLinuxKeyring", System.StringComparison.Ordinal);
        var unprotectedIdx = source.IndexOf("WithUnprotectedFile", System.StringComparison.Ordinal);

        if (unprotectedIdx >= 0)
        {
            keyringIdx.Should().BeLessThan(
                unprotectedIdx,
                "WithUnprotectedFile must only appear after the Linux keyring attempt (as a fallback)");
        }
    }
}
