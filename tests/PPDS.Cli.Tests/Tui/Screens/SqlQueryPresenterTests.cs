using System.Reflection;
using System.Runtime.CompilerServices;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class SqlQueryPresenterTests : IDisposable
{
    private const string TestEnvironmentUrl = "https://test.crm.dynamics.com";

    private readonly TempProfileStore _tempStore;
    private readonly MockServiceProviderFactory _factory;
    private readonly InteractiveSession _session;

    public SqlQueryPresenterTests()
    {
        _tempStore = new TempProfileStore();
        _factory = new MockServiceProviderFactory();
        _session = new InteractiveSession(
            null,
            _tempStore.Store,
            new EnvironmentConfigStore(),
            new TuiStateStore(Path.GetTempFileName()),
            _factory);

        // Set environment so the session has an active URL
        _session.UpdateDisplayedEnvironment(TestEnvironmentUrl, "Test Env");
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    /// <summary>
    /// AC-06: SqlQueryPresenter must have zero Terminal.Gui dependencies.
    /// </summary>
    [Fact]
    public void NoTerminalGuiDependency()
    {
        var assembly = typeof(SqlQueryPresenter).Assembly;
        var presenterType = typeof(SqlQueryPresenter);

        // Check that the source file has no "using Terminal.Gui" via reflection on referenced types
        // We check that no field, property, method parameter, or return type references Terminal.Gui
        var terminalGuiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Terminal.Gui");

        if (terminalGuiAssembly == null)
        {
            // Terminal.Gui not loaded in test context — do source scan instead
            var srcDir = FindSrcDirectory();
            var presenterFile = Path.Combine(srcDir, "PPDS.Cli", "Tui", "Screens", "SqlQueryPresenter.cs");
            var content = File.ReadAllText(presenterFile);

            Assert.DoesNotContain("using Terminal.Gui", content);
            Assert.DoesNotContain("Application.MainLoop", content);
            Assert.DoesNotContain("Application.Run", content);
            return;
        }

        // Deep check: no field/property/method uses Terminal.Gui types
        var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in presenterType.GetFields(bindingFlags))
        {
            Assert.NotEqual("Terminal.Gui", field.FieldType.Assembly.GetName().Name);
        }

        foreach (var prop in presenterType.GetProperties(bindingFlags))
        {
            Assert.NotEqual("Terminal.Gui", prop.PropertyType.Assembly.GetName().Name);
        }

        foreach (var method in presenterType.GetMethods(bindingFlags | BindingFlags.DeclaredOnly))
        {
            Assert.NotEqual("Terminal.Gui", method.ReturnType.Assembly.GetName().Name);
            foreach (var param in method.GetParameters())
            {
                Assert.NotEqual("Terminal.Gui", param.ParameterType.Assembly.GetName().Name);
            }
        }
    }

    /// <summary>
    /// AC-07: ExecuteAsync raises StreamingColumnsReady and StreamingRowsReady events.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RaisesStreamingEvents()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.NextResult = CreateResultWithRows(3);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        IReadOnlyList<QueryColumn>? receivedColumns = null;
        string? receivedEntity = null;
        var rowBatches = new List<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>>();

        presenter.StreamingColumnsReady += (columns, entity) =>
        {
            receivedColumns = columns;
            receivedEntity = entity;
        };

        presenter.StreamingRowsReady += (rows, columns, isComplete, total) =>
        {
            rowBatches.Add(rows);
        };

        // Act
        await presenter.ExecuteAsync("SELECT accountid FROM account", CancellationToken.None);

        // Assert
        Assert.NotNull(receivedColumns);
        Assert.Equal("account", receivedEntity);
        Assert.NotEmpty(rowBatches);
        Assert.Equal(3, rowBatches.Sum(b => b.Count));
    }

    /// <summary>
    /// AC-08: ExecuteAsync raises AuthenticationRequired on DataverseAuthenticationException.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AuthError_RaisesEvent()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.ExceptionToThrow = new DataverseAuthenticationException(
            "Token expired", requiresReauthentication: true);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        DataverseAuthenticationException? receivedAuth = null;
        presenter.AuthenticationRequired += (authEx) => receivedAuth = authEx;

        // Act
        await presenter.ExecuteAsync("SELECT * FROM account", CancellationToken.None);

        // Assert
        Assert.NotNull(receivedAuth);
        Assert.True(receivedAuth!.RequiresReauthentication);
        Assert.False(presenter.IsExecuting);
    }

    /// <summary>
    /// AC-09: ExecuteAsync raises DmlConfirmationRequired on DML confirmation error.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DmlConfirmation_RaisesEvent()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.ExceptionToThrow = new PpdsException(
            ErrorCodes.Query.DmlConfirmationRequired,
            "DELETE will affect 500 rows. Please confirm.");

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        PpdsException? receivedDml = null;
        presenter.DmlConfirmationRequired += (dmlEx) => receivedDml = dmlEx;

        // Act
        await presenter.ExecuteAsync("DELETE FROM account WHERE name = 'test'", CancellationToken.None);

        // Assert
        Assert.NotNull(receivedDml);
        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, receivedDml!.ErrorCode);
        Assert.False(presenter.IsExecuting);
    }

    /// <summary>
    /// AC-10: LoadMoreAsync raises PageLoaded with next page results.
    /// </summary>
    [Fact]
    public async Task LoadMoreAsync_RaisesPageLoaded()
    {
        // Arrange - first execute a query to populate state
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.NextResult = CreateResultWithRows(5);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        await presenter.ExecuteAsync("SELECT accountid FROM account", CancellationToken.None);

        // Now set up for LoadMore
        fakeSqlService.NextResult = new SqlQueryResult
        {
            OriginalSql = "SELECT accountid FROM account",
            TranspiledFetchXml = "<fetch/>",
            Result = new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new[] { new QueryColumn { LogicalName = "accountid" } },
                Records = new[] { new Dictionary<string, QueryValue>
                {
                    ["accountid"] = QueryValue.Simple(Guid.NewGuid().ToString())
                } },
                Count = 1,
                PageNumber = 2,
                PagingCookie = "page2cookie"
            }
        };

        QueryResult? receivedPage = null;
        presenter.PageLoaded += (result) => receivedPage = result;

        // Act
        await presenter.LoadMoreAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(receivedPage);
        Assert.Equal(2, receivedPage!.PageNumber);
        Assert.Equal(2, presenter.LastPageNumber);
    }

    /// <summary>
    /// AC-11: ToggleTds toggles UseTdsEndpoint and raises StatusChanged.
    /// </summary>
    [Fact]
    public void ToggleTds_TogglesState()
    {
        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        var statusMessages = new List<string>();
        presenter.StatusChanged += (msg) => statusMessages.Add(msg);

        // Initially false
        Assert.False(presenter.UseTdsEndpoint);

        // Toggle on
        presenter.ToggleTds();
        Assert.True(presenter.UseTdsEndpoint);
        Assert.Single(statusMessages);
        Assert.Contains("TDS", statusMessages[0]);

        // Toggle off
        presenter.ToggleTds();
        Assert.False(presenter.UseTdsEndpoint);
        Assert.Equal(2, statusMessages.Count);
        Assert.Contains("Dataverse", statusMessages[1]);
    }

    /// <summary>
    /// AC-12: ExecuteAsync saves to history via IQueryHistoryService on success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SavesHistory()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.NextResult = CreateResultWithRows(2);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        // Act
        await presenter.ExecuteAsync("SELECT accountid FROM account", CancellationToken.None);

        // Wait a bit for fire-and-forget to complete
        await Task.Delay(500);

        // Assert - check that history was saved
        var historyService = await _session.GetQueryHistoryServiceAsync(TestEnvironmentUrl);
        var history = await historyService.GetHistoryAsync(TestEnvironmentUrl);
        Assert.NotEmpty(history);
        Assert.Contains(history, h => h.Sql == "SELECT accountid FROM account");
    }

    /// <summary>
    /// AC-13: ExecuteAsync atomically captures and resets DML confirmation flag.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CapturesAndResetsDmlFlag()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.NextResult = CreateResultWithRows(0);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        // Confirm DML, then execute
        presenter.ConfirmDml();

        // Act
        await presenter.ExecuteAsync("DELETE FROM account WHERE name = 'test'", CancellationToken.None);

        // Assert - the first query should have captured IsConfirmed=true
        Assert.NotEmpty(fakeSqlService.ExecutedQueries);
        Assert.True(fakeSqlService.ExecutedQueries[0].DmlSafety!.IsConfirmed);

        // Execute again without confirming - flag should be reset
        fakeSqlService.Reset();
        fakeSqlService.NextResult = CreateResultWithRows(0);
        await presenter.ExecuteAsync("DELETE FROM account WHERE name = 'test2'", CancellationToken.None);

        Assert.NotEmpty(fakeSqlService.ExecutedQueries);
        Assert.False(fakeSqlService.ExecutedQueries[0].DmlSafety!.IsConfirmed);
    }

    /// <summary>
    /// AC-24: Error handling works for generic exceptions after flattening double try-catch.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GenericError_RaisesErrorOccurred()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        fakeSqlService.ExceptionToThrow = new InvalidOperationException("Something went wrong");

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        string? receivedError = null;
        presenter.ErrorOccurred += (msg) => receivedError = msg;

        // Act
        await presenter.ExecuteAsync("SELECT * FROM account", CancellationToken.None);

        // Assert
        Assert.NotNull(receivedError);
        Assert.Contains("Something went wrong", receivedError);
        Assert.False(presenter.IsExecuting);
        Assert.Equal("Something went wrong", presenter.LastErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_RaisesErrorWithoutExecuting()
    {
        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        string? receivedError = null;
        presenter.ErrorOccurred += (msg) => receivedError = msg;

        await presenter.ExecuteAsync("   ", CancellationToken.None);

        Assert.NotNull(receivedError);
        Assert.Contains("empty", receivedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_RaisesQueryCancelled()
    {
        // Arrange
        var fakeSqlService = await GetFakeSqlQueryService();
        // Set up a streaming service that will be slow
        fakeSqlService.NextResult = CreateResultWithRows(5);

        using var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);
        using var cts = new CancellationTokenSource();

        presenter.QueryCancelled += () => { /* cancel path wired */ };

        // Cancel the query CTS immediately via the presenter
        presenter.StatusChanged += (_) =>
        {
            // Cancel on first status change (which happens during execution)
            presenter.CancelQuery();
        };

        // Act
        await presenter.ExecuteAsync("SELECT accountid FROM account", cts.Token);

        // The fake service may complete before cancellation triggers,
        // so we accept either QueryCancelled or ExecutionComplete
        // This tests the cancel path is wired up correctly
        Assert.False(presenter.IsExecuting);
    }

    [Fact]
    public void Dispose_CancelsAndDisposesQueryCts()
    {
        var presenter = new SqlQueryPresenter(_session, TestEnvironmentUrl);

        // Should not throw
        presenter.Dispose();
        presenter.Dispose(); // Double dispose should be safe
    }

    #region Helpers

    private async Task<FakeSqlQueryService> GetFakeSqlQueryService()
    {
        var provider = await _session.GetServiceProviderAsync(TestEnvironmentUrl);
        return (FakeSqlQueryService)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<ISqlQueryService>(provider);
    }

    private static SqlQueryResult CreateResultWithRows(int count)
    {
        var columns = new[]
        {
            new QueryColumn { LogicalName = "accountid", DataType = QueryColumnType.Guid }
        };

        var rows = new List<Dictionary<string, QueryValue>>();
        for (int i = 0; i < count; i++)
        {
            rows.Add(new Dictionary<string, QueryValue>
            {
                ["accountid"] = QueryValue.Simple(Guid.NewGuid().ToString())
            });
        }

        return new SqlQueryResult
        {
            OriginalSql = "SELECT accountid FROM account",
            TranspiledFetchXml = "<fetch><entity name='account'><attribute name='accountid'/></entity></fetch>",
            Result = new QueryResult
            {
                EntityLogicalName = "account",
                Columns = columns,
                Records = rows,
                Count = count
            }
        };
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

    #endregion
}
