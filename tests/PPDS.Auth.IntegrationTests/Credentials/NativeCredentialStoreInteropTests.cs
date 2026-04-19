using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.IntegrationTests.Credentials;

// Leaked entries from killed test runs (SIGKILL / ungraceful exit mid-test) can
// accumulate in the OS credential store as `ppds-interop-test-<guid>`. Normal
// test exit always cleans up via the finally block below. A manual sweep is not
// possible without an enumerate API on ISecureCredentialStore, and we do not
// invent one just for tests. If accumulation becomes a CI problem, the right
// fix is to add enumeration to the public surface (tracked separately).
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
            // Best-effort cleanup: swallow removal errors (already-deleted is fine,
            // and we never want finally to mask the original test failure).
            try { await store.RemoveAsync(appId); }
            catch { /* ignore */ }
        }
    }
}
