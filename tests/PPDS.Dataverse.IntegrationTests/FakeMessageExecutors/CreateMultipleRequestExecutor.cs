using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.FakeMessageExecutors;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace PPDS.Dataverse.IntegrationTests.FakeMessageExecutors;

/// <summary>
/// FakeXrmEasy message executor for CreateMultipleRequest.
/// Creates each entity in the Targets collection and returns the created IDs.
/// </summary>
public class CreateMultipleRequestExecutor : IFakeMessageExecutor
{
    public bool CanExecute(OrganizationRequest request) => request is CreateMultipleRequest;

    public Type GetResponsibleRequestType() => typeof(CreateMultipleRequest);

    public OrganizationResponse Execute(OrganizationRequest request, IXrmFakedContext ctx)
    {
        var createMultiple = (CreateMultipleRequest)request;
        var targets = createMultiple.Targets;
        var service = ctx.GetOrganizationService();
        var createdIds = new List<Guid>();

        foreach (var entity in targets.Entities)
        {
            var id = service.Create(entity);
            createdIds.Add(id);
        }

        return new CreateMultipleResponse
        {
            Results = { { "Ids", createdIds.ToArray() } }
        };
    }
}
