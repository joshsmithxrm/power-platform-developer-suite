using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests;

[Trait("Category", "Unit")]
[Collection(EnvironmentVariableMutatingCollection.Name)]
public sealed class EnvironmentVariableAuthTests : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new();

    private void SetEnvVar(string name, string? value)
    {
        _originalValues.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private void ClearAllEnvVars()
    {
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, null);
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, null);
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, null);
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, null);
        SetEnvVar(EnvironmentVariableAuth.CloudVar, null);
    }

    public void Dispose()
    {
        foreach (var (name, original) in _originalValues)
            Environment.SetEnvironmentVariable(name, original);
    }

    [Fact]
    public void TryCreateProfile_AllVarsSet_ReturnsSyntheticProfile()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");

        var result = EnvironmentVariableAuth.TryCreateProfile();

        result.Should().NotBeNull();
        var (profile, secret) = result!.Value;
        profile.AuthMethod.Should().Be(AuthMethod.ClientSecret);
        profile.ApplicationId.Should().Be("test-client-id");
        profile.TenantId.Should().Be("test-tenant-id");
        profile.Environment.Should().NotBeNull();
        profile.Environment!.Url.Should().Be("https://org.crm.dynamics.com");
        profile.Cloud.Should().Be(CloudEnvironment.Public);
        secret.Should().Be("test-secret");
    }

    [Fact]
    public void TryCreateProfile_AllVarsSet_WithCloud_UsesSpecifiedCloud()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");
        SetEnvVar(EnvironmentVariableAuth.CloudVar, "UsGov");

        var result = EnvironmentVariableAuth.TryCreateProfile();

        result.Should().NotBeNull();
        result!.Value.Profile.Cloud.Should().Be(CloudEnvironment.UsGov);
    }

    [Fact]
    public void TryCreateProfile_NoVarsSet_ReturnsNull()
    {
        ClearAllEnvVars();

        var result = EnvironmentVariableAuth.TryCreateProfile();

        result.Should().BeNull();
    }

    [Fact]
    public void TryCreateProfile_PartialVars_ThrowsWithMissingList()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        // ClientSecret, TenantId, EnvironmentUrl are missing

        var act = () => EnvironmentVariableAuth.TryCreateProfile();

        act.Should().Throw<AuthenticationException>()
            .Where(e => e.ErrorCode == "Auth.IncompleteEnvironmentConfig")
            .WithMessage($"*{EnvironmentVariableAuth.ClientSecretVar}*")
            .WithMessage($"*{EnvironmentVariableAuth.TenantIdVar}*")
            .WithMessage($"*{EnvironmentVariableAuth.EnvironmentUrlVar}*");
    }

    [Fact]
    public void TryCreateProfile_WhitespaceVars_TreatedAsNotSet()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, " ");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");

        var act = () => EnvironmentVariableAuth.TryCreateProfile();

        act.Should().Throw<AuthenticationException>()
            .Where(e => e.ErrorCode == "Auth.IncompleteEnvironmentConfig")
            .WithMessage($"*{EnvironmentVariableAuth.ClientIdVar}*");
    }

    [Fact]
    public void TryCreateProfile_InvalidUrl_ThrowsInvalidEnvironmentUrl()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "http://org.crm.dynamics.com");

        var act = () => EnvironmentVariableAuth.TryCreateProfile();

        act.Should().Throw<AuthenticationException>()
            .Where(e => e.ErrorCode == "Auth.InvalidEnvironmentUrl");
    }

    [Fact]
    public void TryCreateProfile_InvalidCloud_ThrowsInvalidCloud()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");
        SetEnvVar(EnvironmentVariableAuth.CloudVar, "invalid");

        var act = () => EnvironmentVariableAuth.TryCreateProfile();

        act.Should().Throw<AuthenticationException>()
            .Where(e => e.ErrorCode == "Auth.InvalidCloud");
    }

    [Fact]
    public void TryCreateProfile_TrailingSlash_Normalized()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com/");

        var result = EnvironmentVariableAuth.TryCreateProfile();

        result.Should().NotBeNull();
        result!.Value.Profile.Environment!.Url.Should().Be("https://org.crm.dynamics.com");
    }

    [Fact]
    public void TryCreateProfile_CloudOnlySet_NoRequiredVars_ReturnsNull()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.CloudVar, "UsGov");

        var result = EnvironmentVariableAuth.TryCreateProfile();

        result.Should().BeNull();
    }

    /// <summary>
    /// AC-10: Env var auth takes precedence — TryCreateProfile returns non-null
    /// even when PPDS_PROFILE is set. ConnectionResolver calls TryCreateProfile
    /// before ProfileResolver, so env vars structurally win.
    /// </summary>
    [Fact]
    public void TryCreateProfile_EnvVarAuth_TakesPrecedence_OverPpdsProfile()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "env-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "env-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "env-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://env.crm.dynamics.com");
        // Also set PPDS_PROFILE — env var auth should still win
        SetEnvVar("PPDS_PROFILE", "my-named-profile");

        var result = EnvironmentVariableAuth.TryCreateProfile();

        // TryCreateProfile is independent of PPDS_PROFILE — it always returns
        // when the 4 required vars are set. ConnectionResolver checks this first.
        result.Should().NotBeNull();
        result!.Value.Profile.ApplicationId.Should().Be("env-client-id");
    }

    /// <summary>
    /// AC-11: Synthetic profile from env vars produces a working
    /// ClientSecretCredentialProvider via CredentialProviderFactory.
    /// </summary>
    [Fact]
    public async Task SyntheticProfile_CreatesClientSecretProvider()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");
        // Clear PPDS_SPN_SECRET so it doesn't interfere
        SetEnvVar(CredentialProviderFactory.SpnSecretEnvVar, null);
        SetEnvVar(CredentialProviderFactory.TestClientSecretEnvVar, null);

        var envAuth = EnvironmentVariableAuth.TryCreateProfile();
        envAuth.Should().NotBeNull();

        var (profile, clientSecret) = envAuth!.Value;

        using var provider = await CredentialProviderFactory.CreateAsync(
            profile,
            credentialStore: null,
            clientSecretOverride: clientSecret);

        provider.Should().BeOfType<ClientSecretCredentialProvider>();
        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
        provider.Identity.Should().Be("test-client-id");
        provider.TenantId.Should().Be("test-tenant-id");
    }

    /// <summary>
    /// AC-12: When env var auth is used with clientSecretOverride, the credential
    /// store is never accessed. Verified by passing a store that throws on any call.
    /// </summary>
    [Fact]
    public async Task NoCredentialStoreAccess_WhenClientSecretOverrideProvided()
    {
        ClearAllEnvVars();
        SetEnvVar(EnvironmentVariableAuth.ClientIdVar, "test-client-id");
        SetEnvVar(EnvironmentVariableAuth.ClientSecretVar, "test-secret");
        SetEnvVar(EnvironmentVariableAuth.TenantIdVar, "test-tenant-id");
        SetEnvVar(EnvironmentVariableAuth.EnvironmentUrlVar, "https://org.crm.dynamics.com");
        SetEnvVar(CredentialProviderFactory.SpnSecretEnvVar, null);
        SetEnvVar(CredentialProviderFactory.TestClientSecretEnvVar, null);

        var envAuth = EnvironmentVariableAuth.TryCreateProfile();
        envAuth.Should().NotBeNull();

        var (profile, clientSecret) = envAuth!.Value;

        // Pass a credential store that throws on any access
        var throwingStore = new ThrowingCredentialStore();

        using var provider = await CredentialProviderFactory.CreateAsync(
            profile,
            credentialStore: throwingStore,
            clientSecretOverride: clientSecret);

        // If we reached here, the store was never accessed
        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
    }

    /// <summary>
    /// Mock credential store that throws on any method call.
    /// Used to verify the store is never accessed during env var auth.
    /// </summary>
    private sealed class ThrowingCredentialStore : ISecureCredentialStore
    {
        public bool IsCleartextCachingEnabled => throw new InvalidOperationException("Store should not be accessed");

        public Task StoreAsync(StoredCredential credential, CancellationToken ct = default)
            => throw new InvalidOperationException("Store should not be accessed");

        public Task<StoredCredential?> GetAsync(string applicationId, CancellationToken ct = default)
            => throw new InvalidOperationException("Store should not be accessed");

        public Task<bool> RemoveAsync(string applicationId, CancellationToken ct = default)
            => throw new InvalidOperationException("Store should not be accessed");

        public Task ClearAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Store should not be accessed");

        public Task<bool> ExistsAsync(string applicationId, CancellationToken ct = default)
            => throw new InvalidOperationException("Store should not be accessed");
    }
}
