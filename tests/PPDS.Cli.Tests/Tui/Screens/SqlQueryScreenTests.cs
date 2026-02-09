using PPDS.Auth.Profiles;
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
        _session = new InteractiveSession(null, _tempStore.Store, new EnvironmentConfigStore(), new MockServiceProviderFactory());
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
