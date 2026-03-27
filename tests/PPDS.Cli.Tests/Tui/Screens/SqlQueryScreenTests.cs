using System.IO;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// Tests for SqlQueryScreen title behavior using a stub that mirrors the
/// actual Title logic. SqlQueryScreen cannot be instantiated without
/// Application.Init() due to Terminal.Gui View internals, so we verify the
/// title pattern through a lightweight stub inheriting TuiScreenBase.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class SqlQueryScreenTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;

    public SqlQueryScreenTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new EnvironmentConfigStore(), new TuiStateStore(Path.GetTempFileName()), new MockServiceProviderFactory());
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void Title_IncludesCapturedEnvironmentDisplayName()
    {
        // Arrange - set session environment with display name
        _session.UpdateDisplayedEnvironment("https://dev.crm.dynamics.com", "Dev Env");

        // Act
        using var screen = new SqlQueryTitleStub(_session);

        // Assert - title uses the captured display name
        Assert.Equal("SQL Query - Dev Env", screen.Title);
    }

    [Fact]
    public void Title_FallsBackToEnvironmentUrl_WhenDisplayNameIsNull()
    {
        // Arrange - set session environment without display name
        _session.UpdateDisplayedEnvironment("https://dev.crm.dynamics.com", null);

        // Act
        using var screen = new SqlQueryTitleStub(_session);

        // Assert - title falls back to URL
        Assert.Equal("SQL Query - https://dev.crm.dynamics.com", screen.Title);
    }

    [Fact]
    public void Title_IsSqlQuery_WhenNoEnvironmentUrl()
    {
        // Arrange - no environment set on session

        // Act
        using var screen = new SqlQueryTitleStub(_session);

        // Assert
        Assert.Equal("SQL Query", screen.Title);
    }

    [Fact]
    public void Title_UsesCapturedName_NotCurrentSessionName()
    {
        // Arrange - create screen with initial environment
        _session.UpdateDisplayedEnvironment("https://dev.crm.dynamics.com", "Dev Env");
        using var screen = new SqlQueryTitleStub(_session);

        // Act - change session environment after screen creation
        _session.UpdateDisplayedEnvironment("https://prod.crm.dynamics.com", "Prod Env");

        // Assert - title still uses the original captured name
        Assert.Equal("SQL Query - Dev Env", screen.Title);
        // Session has moved on
        Assert.Equal("Prod Env", _session.CurrentEnvironmentDisplayName);
    }

    /// <summary>
    /// AC-25: Menu items and hotkey registrations must use ErrorService.FireAndForget
    /// instead of unmonitored fire-and-forget patterns like "_ = Task".
    /// </summary>
    [Fact]
    public void MenuItems_UseFireAndForget()
    {
        var srcDir = FindSrcDirectory();
        var screenFile = Path.Combine(srcDir, "PPDS.Cli", "Tui", "Screens", "SqlQueryScreen.cs");
        var lines = File.ReadAllLines(screenFile);

        var violations = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("_ = ") && !lines[i].TrimStart().StartsWith("//"))
            {
                violations.Add($"Line {i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"SqlQueryScreen contains unmonitored fire-and-forget patterns (use ErrorService.FireAndForget instead):\n{string.Join("\n", violations)}");
    }

    private static string FindSrcDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate))
                return srcCandidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find src/ directory");
    }

    /// <summary>
    /// Stub that mirrors SqlQueryScreen's Title logic exactly:
    ///   EnvironmentUrl != null ? $"SQL Query - {EnvironmentDisplayName ?? EnvironmentUrl}" : "SQL Query"
    /// This avoids needing Application.Init() while verifying the title format contract.
    /// </summary>
    private sealed class SqlQueryTitleStub : TuiScreenBase
    {
        public override string Title => EnvironmentUrl != null
            ? $"SQL Query - {EnvironmentDisplayName ?? EnvironmentUrl}"
            : "SQL Query";

        public SqlQueryTitleStub(InteractiveSession session)
            : base(session) { }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }
    }
}
