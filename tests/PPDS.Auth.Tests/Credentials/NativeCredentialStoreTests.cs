using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GitCredentialManager;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Unit tests for <see cref="NativeCredentialStore"/>.
/// Uses a mock ICredentialStore to avoid OS credential store access during tests.
/// </summary>
public sealed class NativeCredentialStoreTests
{
    private readonly MockCredentialStore _mockStore;
    private readonly NativeCredentialStore _store;

    public NativeCredentialStoreTests()
    {
        _mockStore = new MockCredentialStore();
        _store = new NativeCredentialStore(false, _mockStore);
    }

    [Fact]
    public async Task StoreAsync_StoresCredential_CanBeRetrieved()
    {
        // Arrange
        var credential = new StoredCredential
        {
            ApplicationId = "test-app-id",
            ClientSecret = "test-secret"
        };

        // Act
        await _store.StoreAsync(credential);
        var retrieved = await _store.GetAsync("test-app-id");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("test-app-id", retrieved.ApplicationId);
        Assert.Equal("test-secret", retrieved.ClientSecret);
    }

    [Fact]
    public async Task StoreAsync_WithCertificate_StoresBothPathAndPassword()
    {
        // Arrange
        var credential = new StoredCredential
        {
            ApplicationId = "cert-app",
            CertificatePath = "/path/to/cert.pfx",
            CertificatePassword = "cert-password"
        };

        // Act
        await _store.StoreAsync(credential);
        var retrieved = await _store.GetAsync("cert-app");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("/path/to/cert.pfx", retrieved.CertificatePath);
        Assert.Equal("cert-password", retrieved.CertificatePassword);
    }

    [Fact]
    public async Task GetAsync_CaseInsensitive_ReturnsCredential()
    {
        // Arrange
        var credential = new StoredCredential
        {
            ApplicationId = "Test-App-ID",
            ClientSecret = "secret"
        };
        await _store.StoreAsync(credential);

        // Act - lookup with different casing
        var retrieved = await _store.GetAsync("test-app-id");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("secret", retrieved.ClientSecret);
        // Note: ApplicationId uses the lookup key, not the original stored casing
    }

    [Fact]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _store.GetAsync("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_ExistingCredential_ReturnsTrue()
    {
        // Arrange
        var credential = new StoredCredential
        {
            ApplicationId = "to-remove",
            ClientSecret = "secret"
        };
        await _store.StoreAsync(credential);

        // Act
        var removed = await _store.RemoveAsync("to-remove");

        // Assert
        Assert.True(removed);
        Assert.Null(await _store.GetAsync("to-remove"));
    }

    [Fact]
    public async Task RemoveAsync_NonExistent_ReturnsFalse()
    {
        // Act
        var removed = await _store.RemoveAsync("non-existent");

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public async Task ExistsAsync_ExistingCredential_ReturnsTrue()
    {
        // Arrange
        var credential = new StoredCredential
        {
            ApplicationId = "exists-test",
            ClientSecret = "secret"
        };
        await _store.StoreAsync(credential);

        // Act
        var exists = await _store.ExistsAsync("exists-test");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_NonExistent_ReturnsFalse()
    {
        // Act
        var exists = await _store.ExistsAsync("non-existent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllCredentials()
    {
        // Arrange
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app1", ClientSecret = "s1" });
        await _store.StoreAsync(new StoredCredential { ApplicationId = "app2", ClientSecret = "s2" });

        // Act
        await _store.ClearAsync();

        // Assert
        Assert.Null(await _store.GetAsync("app1"));
        Assert.Null(await _store.GetAsync("app2"));
    }

    [Fact]
    public async Task StoreAsync_UpdatesExistingCredential()
    {
        // Arrange
        var original = new StoredCredential
        {
            ApplicationId = "update-test",
            ClientSecret = "original-secret"
        };
        await _store.StoreAsync(original);

        // Act
        var updated = new StoredCredential
        {
            ApplicationId = "update-test",
            ClientSecret = "updated-secret"
        };
        await _store.StoreAsync(updated);
        var retrieved = await _store.GetAsync("update-test");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("updated-secret", retrieved.ClientSecret);
    }

    [Fact]
    public void IsCleartextCachingEnabled_WithoutFallback_ReturnsFalse()
    {
        // Assert
        Assert.False(_store.IsCleartextCachingEnabled);
    }

    /// <summary>
    /// Mock implementation of ICredentialStore for testing.
    /// </summary>
    private sealed class MockCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, MockCredential> _credentials = new(StringComparer.OrdinalIgnoreCase);

        public ICredential? Get(string service, string account)
        {
            var key = $"{service}|{account}";
            return _credentials.TryGetValue(key, out var cred) ? cred : null;
        }

        public void AddOrUpdate(string service, string account, string secret)
        {
            var key = $"{service}|{account}";
            _credentials[key] = new MockCredential(account, secret);
        }

        public bool Remove(string service, string account)
        {
            var key = $"{service}|{account}";
            return _credentials.Remove(key);
        }

        public IList<string> GetAccounts(string service)
        {
            var prefix = $"{service}|";
            var accounts = new List<string>();
            foreach (var key in _credentials.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    accounts.Add(key.Substring(prefix.Length));
                }
            }
            return accounts;
        }

        private sealed class MockCredential : ICredential
        {
            public MockCredential(string account, string password)
            {
                Account = account;
                Password = password;
            }

            public string Account { get; }
            public string Password { get; }
        }
    }
}
