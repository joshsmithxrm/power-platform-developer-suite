using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.FakeMessageExecutors;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace PPDS.Dataverse.IntegrationTests.FakeMessageExecutors;

/// <summary>
/// FakeXrmEasy message executor for UpsertMultipleRequest.
/// Upserts each entity in the Targets collection - creates if not exists, updates if exists.
/// </summary>
public class UpsertMultipleRequestExecutor : IFakeMessageExecutor
{
    public bool CanExecute(OrganizationRequest request) => request is UpsertMultipleRequest;

    public Type GetResponsibleRequestType() => typeof(UpsertMultipleRequest);

    public OrganizationResponse Execute(OrganizationRequest request, IXrmFakedContext ctx)
    {
        var upsertMultiple = (UpsertMultipleRequest)request;
        var targets = upsertMultiple.Targets;
        var service = ctx.GetOrganizationService();
        var results = new List<UpsertResponse>();

        foreach (var entity in targets.Entities)
        {
            var upsertRequest = new UpsertRequest { Target = entity };
            var upsertResponse = (UpsertResponse)service.Execute(upsertRequest);
            results.Add(upsertResponse);
        }

        // UpsertMultipleResponse.Results is a direct property that returns the array
        // We need to set it via the Parameters collection
        var response = new UpsertMultipleResponse();
        response["Results"] = results.ToArray();
        return response;
    }
}
