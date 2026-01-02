using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Base class for live Dataverse integration tests.
/// Provides shared configuration and setup/teardown support.
/// </summary>
[Collection("LiveDataverse")]
[Trait("Category", "Integration")]
public abstract class LiveTestBase : IAsyncLifetime
{
    /// <summary>
    /// Configuration containing credentials and connection details.
    /// </summary>
    protected LiveTestConfiguration Configuration { get; }

    /// <summary>
    /// Initializes a new instance of the live test base.
    /// </summary>
    protected LiveTestBase()
    {
        Configuration = new LiveTestConfiguration();
    }

    /// <summary>
    /// Async initialization called before each test.
    /// Override to perform custom setup.
    /// </summary>
    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Async cleanup called after each test.
    /// Override to perform custom teardown.
    /// </summary>
    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Collection definition for live Dataverse tests.
/// Tests in this collection run sequentially to avoid overwhelming the API.
/// </summary>
[CollectionDefinition("LiveDataverse")]
public class LiveDataverseCollection : ICollectionFixture<LiveDataverseFixture>
{
}

/// <summary>
/// Shared fixture for live Dataverse tests.
/// Created once per test collection and shared across all tests.
/// </summary>
public class LiveDataverseFixture : IAsyncLifetime
{
    /// <summary>
    /// Configuration for the live tests.
    /// </summary>
    public LiveTestConfiguration Configuration { get; }

    /// <summary>
    /// Initializes the fixture.
    /// </summary>
    public LiveDataverseFixture()
    {
        Configuration = new LiveTestConfiguration();
    }

    /// <summary>
    /// Called once before any tests in the collection run.
    /// </summary>
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called once after all tests in the collection have run.
    /// </summary>
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
