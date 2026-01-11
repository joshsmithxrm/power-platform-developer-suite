using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for PreAuthDialogResult enum values.
/// </summary>
public sealed class PreAuthDialogResultTests
{
    [Fact]
    public void PreAuthDialogResult_HasExpectedValues()
    {
        // Verify enum has expected values
        Assert.Equal(0, (int)PreAuthDialogResult.OpenBrowser);
        Assert.Equal(1, (int)PreAuthDialogResult.UseDeviceCode);
        Assert.Equal(2, (int)PreAuthDialogResult.Cancel);
    }

    [Fact]
    public void PreAuthDialogResult_DefaultValueIsOpenBrowser()
    {
        // Default enum value should be OpenBrowser (0)
        PreAuthDialogResult result = default;
        Assert.Equal(PreAuthDialogResult.OpenBrowser, result);
    }
}
