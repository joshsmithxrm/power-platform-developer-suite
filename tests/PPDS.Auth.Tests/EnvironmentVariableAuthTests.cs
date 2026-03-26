using System;
using System.Collections.Generic;
using FluentAssertions;
using PPDS.Auth;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests;

[Trait("Category", "Unit")]
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
}
