using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.Enums;
using FakeXrmEasy.Middleware;
using FakeXrmEasy.Middleware.Crud;
using FakeXrmEasy.Middleware.Messages;
using Microsoft.Xrm.Sdk;

namespace PPDS.Dataverse.IntegrationTests;

/// <summary>
/// Base class for integration tests using FakeXrmEasy to mock Dataverse operations.
/// Provides an in-memory IOrganizationService for testing CRUD and message operations.
/// </summary>
public abstract class FakeXrmEasyTestsBase : IDisposable
{
    /// <summary>
    /// The FakeXrmEasy context providing the mocked Dataverse environment.
    /// </summary>
    protected IXrmFakedContext Context { get; }

    /// <summary>
    /// The mocked IOrganizationService for executing Dataverse operations.
    /// </summary>
    protected IOrganizationService Service { get; }

    /// <summary>
    /// Initializes a new instance of the test base with FakeXrmEasy middleware.
    /// </summary>
    protected FakeXrmEasyTestsBase()
    {
        Context = MiddlewareBuilder
            .New()
            .AddCrud()
            .AddFakeMessageExecutors()
            .UseCrud()
            .UseMessages()
            .SetLicense(FakeXrmEasyLicense.RPL_1_5)
            .Build();

        Service = Context.GetOrganizationService();
    }

    /// <summary>
    /// Initializes the context with a set of pre-existing entities.
    /// </summary>
    /// <param name="entities">The entities to seed the context with.</param>
    protected void InitializeWith(params Entity[] entities)
    {
        Context.Initialize(entities);
    }

    /// <summary>
    /// Initializes the context with a collection of pre-existing entities.
    /// </summary>
    /// <param name="entities">The entities to seed the context with.</param>
    protected void InitializeWith(IEnumerable<Entity> entities)
    {
        Context.Initialize(entities);
    }

    /// <summary>
    /// Disposes of any resources used by the test.
    /// </summary>
    public virtual void Dispose()
    {
        // IXrmFakedContext doesn't declare IDisposable, but runtime type may implement it
        (Context as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
