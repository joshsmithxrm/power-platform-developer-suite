using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Discovery;

public class GlobalDiscoveryServiceTests
{
    #region SupportsGlobalDiscovery

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser, true)]
    [InlineData(AuthMethod.DeviceCode, true)]
    [InlineData(AuthMethod.ClientSecret, false)]
    [InlineData(AuthMethod.CertificateFile, false)]
    [InlineData(AuthMethod.CertificateStore, false)]
    [InlineData(AuthMethod.ManagedIdentity, false)]
    [InlineData(AuthMethod.GitHubFederated, false)]
    [InlineData(AuthMethod.AzureDevOpsFederated, false)]
    [InlineData(AuthMethod.UsernamePassword, false)]
    public void SupportsGlobalDiscovery_ReturnsExpectedResult(AuthMethod authMethod, bool expected)
    {
        var result = GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod);

        result.Should().Be(expected);
    }

    #endregion

    #region FromProfile validation

    [Fact]
    public void FromProfile_WithInteractiveBrowser_Succeeds()
    {
        var profile = CreateTestProfile(AuthMethod.InteractiveBrowser);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().NotThrow();
    }

    [Fact]
    public void FromProfile_WithDeviceCode_Succeeds()
    {
        var profile = CreateTestProfile(AuthMethod.DeviceCode);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().NotThrow();
    }

    [Fact]
    public void FromProfile_WithClientSecret_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.ClientSecret);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*")
            .WithMessage("*ClientSecret*")
            .WithMessage("*not supported*");
    }

    [Fact]
    public void FromProfile_WithCertificateFile_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.CertificateFile);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*")
            .WithMessage("*CertificateFile*");
    }

    [Fact]
    public void FromProfile_WithCertificateStore_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.CertificateStore);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*");
    }

    [Fact]
    public void FromProfile_WithManagedIdentity_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.ManagedIdentity);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*")
            .WithMessage("*ManagedIdentity*");
    }

    [Fact]
    public void FromProfile_WithGitHubFederated_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.GitHubFederated);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*")
            .WithMessage("*GitHubFederated*");
    }

    [Fact]
    public void FromProfile_WithAzureDevOpsFederated_ThrowsNotSupportedException()
    {
        var profile = CreateTestProfile(AuthMethod.AzureDevOpsFederated);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*interactive user authentication*")
            .WithMessage("*AzureDevOpsFederated*");
    }

    [Fact]
    public void FromProfile_ErrorMessage_IncludesHelpfulGuidance()
    {
        var profile = CreateTestProfile(AuthMethod.ClientSecret);

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*ppds env select*")
            .WithMessage("*ppds auth create*");
    }

    [Fact]
    public void FromProfile_ErrorMessage_IncludesProfileName()
    {
        var profile = CreateTestProfile(AuthMethod.ClientSecret);
        profile.Name = "my-test-profile";

        var act = () => GlobalDiscoveryService.FromProfile(profile);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*my-test-profile*");
    }

    #endregion

    #region UPN redaction regression guards (A5-follow-up)

    /// <summary>
    /// Regression guard: neither `AuthDebugLog` nor `AuthenticationOutput` call site in
    /// GlobalDiscoveryService.cs may print `account.Username` or `result.Account.Username`
    /// without routing through LogIdentityHelper.HashIdentifier. This prevents raw UPNs
    /// from leaking into debug logs or user-visible auth output streams that may be
    /// redirected to CI/bug-report logs.
    /// </summary>
    [Fact]
    public void GlobalDiscoveryService_DoesNotEmitRawUpnToLogsOrAuthOutput()
    {
        var sourcePath = FindSourceFile();
        var source = File.ReadAllText(sourcePath);

        // Match any occurrence of `{...Username}` inside an AuthDebugLog.WriteLine or
        // AuthenticationOutput.WriteLine call that is NOT wrapped by HashIdentifier.
        // The approved form looks like `upn={LogIdentityHelper.HashIdentifier(x.Username)}`.
        var rawUpnPattern = new Regex(
            @"(AuthDebugLog|AuthenticationOutput)\.WriteLine\([^)]*\{[^{}]*\.Username\}[^)]*\)",
            RegexOptions.Singleline);

        foreach (Match match in rawUpnPattern.Matches(source))
        {
            match.Value.Should().Contain("HashIdentifier",
                $"every log/auth-output emission of .Username in GlobalDiscoveryService must be hashed. " +
                $"Offending call: {match.Value}");
        }
    }

    private static string FindSourceFile()
    {
        // Walk up from the test assembly to find src/PPDS.Auth/Discovery/GlobalDiscoveryService.cs.
        // Matches the pattern used elsewhere in the test suite for source-level regression guards.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PPDS.Auth", "Discovery", "GlobalDiscoveryService.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate GlobalDiscoveryService.cs from test base directory.");
    }

    #endregion

    #region Helper methods

    private static AuthProfile CreateTestProfile(AuthMethod authMethod)
    {
        return new AuthProfile
        {
            Index = 0,
            Name = "test-profile",
            AuthMethod = authMethod,
            Cloud = CloudEnvironment.Public
        };
    }

    #endregion
}
