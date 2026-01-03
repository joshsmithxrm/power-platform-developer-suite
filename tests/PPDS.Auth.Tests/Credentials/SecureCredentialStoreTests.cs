using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class SecureCredentialStoreTests : IDisposable
{
    private readonly string _tempFilePath;
    private readonly SecureCredentialStore _store;

    public SecureCredentialStoreTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"test_credentials_{Guid.NewGuid()}.dat");
        _store = new SecureCredentialStore(_tempFilePath, allowCleartextFallback: true);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        var act = () => new SecureCredentialStore(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CacheFilePath_ReturnsConfiguredPath()
    {
        _store.CacheFilePath.Should().Be(_tempFilePath);
    }

    [Fact]
    public async Task StoreAsync_NullCredential_Throws()
    {
        var act = async () => await _store.StoreAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StoreAsync_EmptyApplicationId_Throws()
    {
        var credential = new StoredCredential { ApplicationId = "" };

        var act = async () => await _store.StoreAsync(credential);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StoreAsync_ValidCredential_Succeeds()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret"
        };

        await _store.StoreAsync(credential);

        var retrieved = await _store.GetAsync("app-id");
        retrieved.Should().NotBeNull();
        retrieved!.ClientSecret.Should().Be("secret");
    }

    [Fact]
    public async Task GetAsync_NonexistentId_ReturnsNull()
    {
        var result = await _store.GetAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_NullId_ReturnsNull()
    {
        var result = await _store.GetAsync(null!);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_EmptyId_ReturnsNull()
    {
        var result = await _store.GetAsync("");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_CaseInsensitive()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "APP-ID",
            ClientSecret = "secret"
        };
        await _store.StoreAsync(credential);

        var retrieved = await _store.GetAsync("app-id");

        retrieved.Should().NotBeNull();
        retrieved!.ClientSecret.Should().Be("secret");
    }

    [Fact]
    public async Task StoreAsync_UpdatesExistingCredential()
    {
        var credential1 = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret1"
        };
        await _store.StoreAsync(credential1);

        var credential2 = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret2"
        };
        await _store.StoreAsync(credential2);

        var retrieved = await _store.GetAsync("app-id");
        retrieved!.ClientSecret.Should().Be("secret2");
    }

    [Fact]
    public async Task RemoveAsync_ExistingCredential_ReturnsTrue()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret"
        };
        await _store.StoreAsync(credential);

        var result = await _store.RemoveAsync("app-id");

        result.Should().BeTrue();
        var retrieved = await _store.GetAsync("app-id");
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_NonexistentCredential_ReturnsFalse()
    {
        var result = await _store.RemoveAsync("nonexistent");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_NullId_ReturnsFalse()
    {
        var result = await _store.RemoveAsync(null!);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_RemovesAllCredentials()
    {
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app1", ClientSecret = "s1" });
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app2", ClientSecret = "s2" });

        await _store.ClearAsync();

        (await _store.GetAsync("app1")).Should().BeNull();
        (await _store.GetAsync("app2")).Should().BeNull();
    }

    [Fact]
    public async Task ExistsAsync_ExistingCredential_ReturnsTrue()
    {
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app-id", ClientSecret = "secret" });

        var result = await _store.ExistsAsync("app-id");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonexistentCredential_ReturnsFalse()
    {
        var result = await _store.ExistsAsync("nonexistent");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_NullId_ReturnsFalse()
    {
        var result = await _store.ExistsAsync(null!);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task StoreAsync_WithCertificate_StoresPathAndPassword()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            CertificatePath = "/path/to/cert.pfx",
            CertificatePassword = "certpass"
        };

        await _store.StoreAsync(credential);

        var retrieved = await _store.GetAsync("app-id");
        retrieved.Should().NotBeNull();
        retrieved!.CertificatePath.Should().Be("/path/to/cert.pfx");
        retrieved.CertificatePassword.Should().Be("certpass");
    }

    [Fact]
    public async Task StoreAsync_WithCertificateNoPassword_StoresOnlyPath()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            CertificatePath = "/path/to/cert.pfx"
        };

        await _store.StoreAsync(credential);

        var retrieved = await _store.GetAsync("app-id");
        retrieved.Should().NotBeNull();
        retrieved!.CertificatePath.Should().Be("/path/to/cert.pfx");
        retrieved.CertificatePassword.Should().BeNull();
    }

    [Fact]
    public async Task StoreAsync_WithPassword_StoresPassword()
    {
        var credential = new StoredCredential
        {
            ApplicationId = "user@example.com",
            Password = "userpass"
        };

        await _store.StoreAsync(credential);

        var retrieved = await _store.GetAsync("user@example.com");
        retrieved.Should().NotBeNull();
        retrieved!.Password.Should().Be("userpass");
    }

    [Fact]
    public async Task StoreAsync_MultipleCredentials_StoresAll()
    {
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app1", ClientSecret = "s1" });
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app2", ClientSecret = "s2" });
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app3", ClientSecret = "s3" });

        (await _store.GetAsync("app1"))!.ClientSecret.Should().Be("s1");
        (await _store.GetAsync("app2"))!.ClientSecret.Should().Be("s2");
        (await _store.GetAsync("app3"))!.ClientSecret.Should().Be("s3");
    }

    [Fact]
    public void IsCleartextCachingEnabled_WithFallbackEnabled_ReturnsCorrectValue()
    {
        // Create store with cleartext fallback enabled
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_creds_{Guid.NewGuid()}.dat");
        try
        {
            using var store = new SecureCredentialStore(tempPath, allowCleartextFallback: true);

            // On Linux with fallback enabled, should return true
            // On Windows/macOS, secure storage is always available so returns false
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
            {
                store.IsCleartextCachingEnabled.Should().BeTrue();
            }
            else
            {
                store.IsCleartextCachingEnabled.Should().BeFalse();
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void IsCleartextCachingEnabled_WithFallbackDisabled_ReturnsFalse()
    {
        // Create store with cleartext fallback disabled
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_creds_{Guid.NewGuid()}.dat");
        try
        {
            using var store = new SecureCredentialStore(tempPath, allowCleartextFallback: false);

            // Should always return false when fallback is disabled
            store.IsCleartextCachingEnabled.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void IsCleartextCachingEnabled_DefaultConstructor_ReturnsFalse()
    {
        // Default constructor should have fallback disabled
        // Note: Can't easily test this without affecting real config, so we verify via a custom path
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_creds_{Guid.NewGuid()}.dat");
        try
        {
            // Using the 1-param constructor which defaults allowCleartextFallback to false
            using var store = new SecureCredentialStore(tempPath);

            store.IsCleartextCachingEnabled.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
