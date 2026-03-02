using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class ProfilePathsTests
{
    [Fact]
    public void AppName_IsPPDS()
    {
        ProfilePaths.AppName.Should().Be("PPDS");
    }

    [Fact]
    public void ProfilesFileName_IsProfilesJson()
    {
        ProfilePaths.ProfilesFileName.Should().Be("profiles.json");
    }

    [Fact]
    public void TokenCacheFileName_IsMsalTokenCacheBin()
    {
        ProfilePaths.TokenCacheFileName.Should().Be("msal_token_cache.bin");
    }

    [Fact]
    public void DataDirectory_IsNotEmpty()
    {
        var directory = ProfilePaths.DataDirectory;

        directory.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataDirectory_ContainsAppName()
    {
        var directory = ProfilePaths.DataDirectory;

        directory.Should().ContainEquivalentOf("PPDS", "data directory should contain app name (case-insensitive: Linux uses ~/.ppds)");
    }

    [Fact]
    public void ProfilesFile_EndsWithProfilesJson()
    {
        var path = ProfilePaths.ProfilesFile;

        path.Should().EndWith("profiles.json");
    }

    [Fact]
    public void ProfilesFile_IsInDataDirectory()
    {
        var profilesFile = ProfilePaths.ProfilesFile;
        var dataDirectory = ProfilePaths.DataDirectory;

        profilesFile.Should().StartWith(dataDirectory);
    }

    [Fact]
    public void TokenCacheFile_EndsWithMsalTokenCacheBin()
    {
        var path = ProfilePaths.TokenCacheFile;

        path.Should().EndWith("msal_token_cache.bin");
    }

    [Fact]
    public void TokenCacheFile_IsInDataDirectory()
    {
        var tokenCacheFile = ProfilePaths.TokenCacheFile;
        var dataDirectory = ProfilePaths.DataDirectory;

        tokenCacheFile.Should().StartWith(dataDirectory);
    }

    [Fact]
    public void EnsureDirectoryExists_CreatesDirectory()
    {
        // This test creates a real directory, but it's safe because it's in the user's profile
        var directory = ProfilePaths.DataDirectory;

        ProfilePaths.EnsureDirectoryExists();

        Directory.Exists(directory).Should().BeTrue();
    }

    [Fact]
    public void EnsureDirectoryExists_CalledTwice_DoesNotThrow()
    {
        var act = () =>
        {
            ProfilePaths.EnsureDirectoryExists();
            ProfilePaths.EnsureDirectoryExists();
        };

        act.Should().NotThrow();
    }
}
