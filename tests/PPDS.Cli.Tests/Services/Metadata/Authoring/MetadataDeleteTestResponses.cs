using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>Minimal dependency-free SDK responses for retained mutation/cache characterization tests.</summary>
internal static class MetadataDeleteTestResponses
{
    private static readonly Guid TargetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    internal static OrganizationResponse For(OrganizationRequest request)
    {
        switch (request)
        {
            case RetrieveEntityRequest entity:
                Assert.True(entity.RetrieveAsIfPublished); Assert.Equal(EntityFilters.Entity, entity.EntityFilters);
                return new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = new EntityMetadata { LogicalName = entity.LogicalName, MetadataId = TargetId } } };
            case RetrieveOptionSetRequest choice:
                Assert.True(choice.RetrieveAsIfPublished);
                return new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = new OptionSetMetadata { Name = choice.Name, MetadataId = TargetId, IsGlobal = true } } };
            case RetrieveDependenciesForDeleteRequest dependencies:
                Assert.Equal(TargetId, dependencies.ObjectId); Assert.Contains(dependencies.ComponentType, new[] { 1, 9 });
                return new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection() } };
            case DeleteEntityRequest or DeleteOptionSetRequest:
                return new OrganizationResponse();
            default: throw new InvalidOperationException("Unexpected metadata request in deletion characterization: " + request.RequestName);
        }
    }
}
