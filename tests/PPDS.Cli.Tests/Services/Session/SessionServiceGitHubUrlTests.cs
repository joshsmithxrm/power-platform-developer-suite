using PPDS.Cli.Services.Session;
using Xunit;

namespace PPDS.Cli.Tests.Services.Session;

/// <summary>
/// Tests for SessionService.ParseGitHubUrl method.
/// </summary>
public class SessionServiceGitHubUrlTests
{
    #region HTTPS Format Tests

    [Fact]
    public void ParseGitHubUrl_HttpsWithGitSuffix_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("https://github.com/joshsmithxrm/power-platform-developer-suite.git");

        Assert.Equal("joshsmithxrm", owner);
        Assert.Equal("power-platform-developer-suite", repo);
    }

    [Fact]
    public void ParseGitHubUrl_HttpsWithoutGitSuffix_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("https://github.com/owner/repo");

        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    [Fact]
    public void ParseGitHubUrl_HttpsWithTrailingSlash_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("https://github.com/owner/repo/");

        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    #endregion

    #region SSH Format Tests

    [Fact]
    public void ParseGitHubUrl_SshWithGitSuffix_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("git@github.com:joshsmithxrm/power-platform-developer-suite.git");

        Assert.Equal("joshsmithxrm", owner);
        Assert.Equal("power-platform-developer-suite", repo);
    }

    [Fact]
    public void ParseGitHubUrl_SshWithoutGitSuffix_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("git@github.com:owner/repo");

        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ParseGitHubUrl_WithWhitespace_TrimsAndParses()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("  https://github.com/owner/repo.git  ");

        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    [Fact]
    public void ParseGitHubUrl_CaseInsensitiveGitSuffix_ReturnsOwnerAndRepo()
    {
        var (owner, repo) = SessionService.ParseGitHubUrl("https://github.com/owner/repo.GIT");

        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    #endregion

    #region Error Cases

    [Fact]
    public void ParseGitHubUrl_NullUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl(null!));
    }

    [Fact]
    public void ParseGitHubUrl_EmptyUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl(""));
    }

    [Fact]
    public void ParseGitHubUrl_WhitespaceOnly_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl("   "));
    }

    [Fact]
    public void ParseGitHubUrl_InvalidUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl("https://example.com/owner/repo"));
    }

    [Fact]
    public void ParseGitHubUrl_GitLabUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl("https://gitlab.com/owner/repo.git"));
    }

    [Fact]
    public void ParseGitHubUrl_HttpsWithOnlyOwner_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl("https://github.com/owner"));
    }

    [Fact]
    public void ParseGitHubUrl_SshWithOnlyOwner_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SessionService.ParseGitHubUrl("git@github.com:owner"));
    }

    #endregion
}
