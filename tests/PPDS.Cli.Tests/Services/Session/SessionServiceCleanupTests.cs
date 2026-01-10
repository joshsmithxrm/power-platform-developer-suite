using PPDS.Cli.Services.Session;
using Xunit;

namespace PPDS.Cli.Tests.Services.Session;

/// <summary>
/// Tests for SessionService orphaned session cleanup logic.
/// </summary>
public class SessionServiceCleanupTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private static SessionState CreateSession(int issueNumber, string worktreePath)
    {
        return new SessionState
        {
            Id = issueNumber.ToString(),
            IssueNumber = issueNumber,
            IssueTitle = $"Test issue #{issueNumber}",
            Status = SessionStatus.Working,
            Branch = $"issue-{issueNumber}",
            WorktreePath = worktreePath,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeat = DateTimeOffset.UtcNow
        };
    }

    #region FindOrphanedSessions Tests

    [Fact]
    public void FindOrphanedSessions_WithExistingWorktree_ReturnsEmpty()
    {
        // Arrange
        var existingPath = CreateTempDirectory();
        var sessions = new[] { CreateSession(123, existingPath) };

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert
        Assert.Empty(orphaned);
    }

    [Fact]
    public void FindOrphanedSessions_WithNonExistentWorktree_ReturnsSession()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        var sessions = new[] { CreateSession(456, nonExistentPath) };

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert
        Assert.Single(orphaned);
        Assert.Equal(456, orphaned[0].IssueNumber);
    }

    [Fact]
    public void FindOrphanedSessions_WithMixedSessions_ReturnsOnlyOrphaned()
    {
        // Arrange
        var existingPath = CreateTempDirectory();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");

        var sessions = new[]
        {
            CreateSession(100, existingPath),         // exists - should NOT be in result
            CreateSession(200, nonExistentPath),      // missing - should be in result
            CreateSession(300, existingPath),         // exists - should NOT be in result
        };

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert
        Assert.Single(orphaned);
        Assert.Equal(200, orphaned[0].IssueNumber);
    }

    [Fact]
    public void FindOrphanedSessions_WithAllOrphaned_ReturnsAll()
    {
        // Arrange
        var nonExistent1 = Path.Combine(Path.GetTempPath(), $"missing-1-{Guid.NewGuid():N}");
        var nonExistent2 = Path.Combine(Path.GetTempPath(), $"missing-2-{Guid.NewGuid():N}");

        var sessions = new[]
        {
            CreateSession(10, nonExistent1),
            CreateSession(20, nonExistent2),
        };

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert
        Assert.Equal(2, orphaned.Count);
        Assert.Contains(orphaned, s => s.IssueNumber == 10);
        Assert.Contains(orphaned, s => s.IssueNumber == 20);
    }

    [Fact]
    public void FindOrphanedSessions_WithEmptyInput_ReturnsEmpty()
    {
        // Arrange
        var sessions = Array.Empty<SessionState>();

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert
        Assert.Empty(orphaned);
    }

    [Fact]
    public void FindOrphanedSessions_PreservesStaleSessionsWithExistingWorktree()
    {
        // Arrange - a "stale" session with no heartbeat but worktree exists
        var existingPath = CreateTempDirectory();
        var staleSession = new SessionState
        {
            Id = "789",
            IssueNumber = 789,
            IssueTitle = "Stale session",
            Status = SessionStatus.Working,
            Branch = "issue-789",
            WorktreePath = existingPath,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-5),
            LastHeartbeat = DateTimeOffset.UtcNow.AddHours(-3) // Very old heartbeat
        };
        var sessions = new[] { staleSession };

        // Act
        var orphaned = SessionService.FindOrphanedSessions(sessions);

        // Assert - stale sessions with existing worktrees should NOT be cleaned up
        Assert.Empty(orphaned);
    }

    #endregion
}
