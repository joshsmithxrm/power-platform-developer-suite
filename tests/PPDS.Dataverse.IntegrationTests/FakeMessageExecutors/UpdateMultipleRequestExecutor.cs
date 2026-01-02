using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.FakeMessageExecutors;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace PPDS.Dataverse.IntegrationTests.FakeMessageExecutors;

/// <summary>
/// FakeXrmEasy message executor for UpdateMultipleRequest.
/// Updates each entity in the Targets collection.
/// </summary>
public class UpdateMultipleRequestExecutor : IFakeMessageExecutor
{
    public bool CanExecute(OrganizationRequest request) => request is UpdateMultipleRequest;

    public Type GetResponsibleRequestType() => typeof(UpdateMultipleRequest);

    public OrganizationResponse Execute(OrganizationRequest request, IXrmFakedContext ctx)
    {
        var updateMultiple = (UpdateMultipleRequest)request;
        var targets = updateMultiple.Targets;
        var service = ctx.GetOrganizationService();

        foreach (var entity in targets.Entities)
        {
            service.Update(entity);
        }

        return new UpdateMultipleResponse();
    }
}
