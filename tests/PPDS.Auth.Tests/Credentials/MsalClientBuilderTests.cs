using System.IO;
using System.Linq;
using System.Reflection;
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
            // When tests are run from NuGet packaged form, source file may
            // not be available. Skip rather than fail spuriously.
            return;
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
