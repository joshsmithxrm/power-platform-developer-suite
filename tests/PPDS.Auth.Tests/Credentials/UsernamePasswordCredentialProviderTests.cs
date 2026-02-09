using FluentAssertions;
using PPDS.Auth.Credentials;
using System.Reflection;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for <see cref="UsernamePasswordCredentialProvider"/>.
/// </summary>
public class UsernamePasswordCredentialProviderTests
{
    #region Token Cache URL Tracking

    /// <summary>
    /// Verifies that UsernamePasswordCredentialProvider tracks the URL associated with cached tokens,
    /// matching the pattern in DeviceCodeCredentialProvider and InteractiveBrowserCredentialProvider.
    /// Without URL tracking, a token obtained for one environment could be incorrectly reused
    /// for a different environment URL.
    /// </summary>
    [Fact]
    public void HasCachedResultUrlField_ForTokenScopeMismatchPrevention()
    {
        var field = typeof(UsernamePasswordCredentialProvider)
            .GetField("_cachedResultUrl",
                BindingFlags.NonPublic | BindingFlags.Instance);

        field.Should().NotBeNull(
            because: "without _cachedResultUrl, GetCachedTokenInfoAsync can return stale token info for wrong environment");
    }

    #endregion
}
