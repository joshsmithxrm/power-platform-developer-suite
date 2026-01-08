using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

/// <summary>
/// Tests for <see cref="ProfileResolver"/>.
/// </summary>
/// <remarks>
/// These tests manipulate the PPDS_PROFILE environment variable.
/// Each test cleans up after itself to avoid cross-test interference.
/// </remarks>
public class ProfileResolverTests : IDisposable
{
    private readonly string? _originalEnvValue;

    public ProfileResolverTests()
    {
        // Save original value to restore in Dispose
        _originalEnvValue = Environment.GetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable);
    }

    public void Dispose()
    {
        // Restore original value
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, _originalEnvValue);
    }

    #region GetEffectiveProfileName Tests

    [Fact]
    public void GetEffectiveProfileName_ExplicitProfile_TakesPriority()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "EnvProfile");

        // Act
        var result = ProfileResolver.GetEffectiveProfileName("ExplicitProfile");

        // Assert
        result.Should().Be("ExplicitProfile");
    }

    [Fact]
    public void GetEffectiveProfileName_EnvVar_TakesPriorityOverNull()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "EnvProfile");

        // Act
        var result = ProfileResolver.GetEffectiveProfileName(null);

        // Assert
        result.Should().Be("EnvProfile");
    }

    [Fact]
    public void GetEffectiveProfileName_NoExplicitNoEnvVar_ReturnsNull()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, null);

        // Act
        var result = ProfileResolver.GetEffectiveProfileName(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetEffectiveProfileName_EmptyExplicit_FallsBackToEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "EnvProfile");

        // Act
        var result = ProfileResolver.GetEffectiveProfileName("");

        // Assert
        result.Should().Be("EnvProfile");
    }

    [Fact]
    public void GetEffectiveProfileName_WhitespaceExplicit_FallsBackToEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "EnvProfile");

        // Act
        var result = ProfileResolver.GetEffectiveProfileName("   ");

        // Assert
        result.Should().Be("EnvProfile");
    }

    [Fact]
    public void GetEffectiveProfileName_EmptyEnvVar_ReturnsNull()
    {
        // Arrange
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "");

        // Act
        var result = ProfileResolver.GetEffectiveProfileName(null);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ResolveProfile Tests

    [Fact]
    public void ResolveProfile_ExplicitProfile_ReturnsMatchingProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "Profile1" };
        var profile2 = new AuthProfile { Name = "Profile2" };
        collection.Add(profile1);
        collection.Add(profile2);
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, null);

        // Act
        var result = ProfileResolver.ResolveProfile(collection, "Profile2");

        // Assert
        result.Should().Be(profile2);
    }

    [Fact]
    public void ResolveProfile_EnvVar_ReturnsMatchingProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "Profile1" };
        var profile2 = new AuthProfile { Name = "EnvProfile" };
        collection.Add(profile1);
        collection.Add(profile2);
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, "EnvProfile");

        // Act
        var result = ProfileResolver.ResolveProfile(collection, null);

        // Assert
        result.Should().Be(profile2);
    }

    [Fact]
    public void ResolveProfile_NoOverride_ReturnsActiveProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "Profile1" };
        var profile2 = new AuthProfile { Name = "Profile2" };
        collection.Add(profile1);
        collection.Add(profile2);
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, null);

        // Act
        var result = ProfileResolver.ResolveProfile(collection, null);

        // Assert - First profile is active by default
        result.Should().Be(profile1);
    }

    [Fact]
    public void ResolveProfile_ProfileNotFound_ReturnsNull()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "Profile1" };
        collection.Add(profile1);
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, null);

        // Act
        var result = ProfileResolver.ResolveProfile(collection, "NonExistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveProfile_ByIndex_ReturnsMatchingProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "Profile1" };
        var profile2 = new AuthProfile { Name = "Profile2" };
        collection.Add(profile1);
        collection.Add(profile2);
        Environment.SetEnvironmentVariable(ProfileResolver.ProfileEnvironmentVariable, null);

        // Act - Use index "2" as string
        var result = ProfileResolver.ResolveProfile(collection, "2");

        // Assert
        result.Should().Be(profile2);
    }

    #endregion
}
