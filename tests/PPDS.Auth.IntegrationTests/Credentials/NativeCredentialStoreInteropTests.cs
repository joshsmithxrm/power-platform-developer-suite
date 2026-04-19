using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.IntegrationTests.Credentials;

[Trait("Category", "Integration")]
public sealed class NativeCredentialStoreInteropTests
{
    [SkippableFact]
    public Task Windows_RoundTripsSecret()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        return RoundTrip();
    }

    [SkippableFact]
    public Task MacOS_RoundTripsSecret()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.OSX));
        return RoundTrip();
    }

    [SkippableFact]
    public Task Linux_RoundTripsSecret()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        Skip.IfNot(Environment.GetEnvironmentVariable("PPDS_TEST_LIBSECRET") == "1");
        return RoundTrip(allowCleartextFallback: false);
    }

    private static async Task RoundTrip(bool allowCleartextFallback = false)
    {
        var store = new NativeCredentialStore(allowCleartextFallback);
        var appId = $"ppds-interop-test-{Guid.NewGuid():N}";
        try
        {
            await store.StoreAsync(new StoredCredential
            {
                ApplicationId = appId,
                ClientSecret = "probe-value-42"
            });
            var retrieved = await store.GetAsync(appId);
            retrieved.Should().NotBeNull();
            retrieved!.ClientSecret.Should().Be("probe-value-42");
        }
        finally
        {
            await store.RemoveAsync(appId);
        }
    }
}
