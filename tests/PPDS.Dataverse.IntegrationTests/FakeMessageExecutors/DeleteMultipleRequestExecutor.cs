using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.FakeMessageExecutors;
using Microsoft.Xrm.Sdk;

namespace PPDS.Dataverse.IntegrationTests.FakeMessageExecutors;

/// <summary>
/// FakeXrmEasy message executor for the DeleteMultiple unbound message.
/// Deletes each entity reference in the Targets collection.
/// Used for elastic table delete operations.
/// </summary>
/// <remarks>
/// DeleteMultiple is an unbound (untyped) Dataverse message, not a typed SDK message class.
/// The request is constructed as: new OrganizationRequest("DeleteMultiple") { Parameters = { { "Targets", entityRefCollection } } }
/// </remarks>
public class DeleteMultipleRequestExecutor : IFakeMessageExecutor
{
    /// <summary>
    /// Optional predicate to simulate failures on specific records.
    /// If set, records matching this predicate will throw an exception.
    /// </summary>
    public static Func<EntityReference, bool>? FailurePredicate { get; set; }

    public bool CanExecute(OrganizationRequest request) =>
        request.RequestName == "DeleteMultiple";

    public Type GetResponsibleRequestType() => typeof(OrganizationRequest);

    public OrganizationResponse Execute(OrganizationRequest request, IXrmFakedContext ctx)
    {
        var targets = request.Parameters["Targets"] as EntityReferenceCollection;
        if (targets == null)
        {
            throw new InvalidOperationException("DeleteMultiple request must have a Targets parameter of type EntityReferenceCollection");
        }

        var service = ctx.GetOrganizationService();

        foreach (var entityRef in targets)
        {
            if (FailurePredicate?.Invoke(entityRef) == true)
            {
                throw new InvalidOperationException($"Simulated failure for {entityRef.LogicalName} {entityRef.Id}");
            }

            service.Delete(entityRef.LogicalName, entityRef.Id);
        }

        return new OrganizationResponse { ResponseName = "DeleteMultiple" };
    }

    /// <summary>
    /// Resets the failure predicate. Call in test cleanup.
    /// </summary>
    public static void ResetFailurePredicate()
    {
        FailurePredicate = null;
    }
}
