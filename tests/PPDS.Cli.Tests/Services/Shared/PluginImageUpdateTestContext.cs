using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tests.Services.Shared;

internal static class PluginImageUpdateTestContext
{
    internal const string Table = "sdkmessageprocessingstepimage";
    internal static readonly string[] Fields =
        ["sdkmessageprocessingstepid", "name", "imagetype", "entityalias", "messagepropertyname", "attributes", "description"];

    internal static Entity Snapshot(Guid id) => new(Table, id)
    {
        RowVersion = "101",
        ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()),
        ["name"] = "Existing image", ["imagetype"] = new OptionSetValue(1),
        ["entityalias"] = "ExistingAlias", ["messagepropertyname"] = "Target",
        ["attributes"] = "accountnumber,name", ["description"] = "Existing description"
    };

    internal static EntityMetadata Metadata()
    {
        var metadata = new EntityMetadata { LogicalName = Table };
        typeof(EntityMetadata).GetProperty("IsOptimisticConcurrencyEnabled")!.SetValue(metadata, true);
        var attributes = Fields.Select(field =>
        {
            var attribute = new StringAttributeMetadata { LogicalName = field };
            typeof(AttributeMetadata).GetProperty("IsValidForRead")!.SetValue(attribute, true);
            typeof(AttributeMetadata).GetProperty("IsValidForUpdate")!.SetValue(attribute, true);
            return (AttributeMetadata)attribute;
        }).ToArray();
        typeof(EntityMetadata).GetProperty("Attributes")!.SetValue(metadata, attributes);
        return metadata;
    }

    internal static RetrieveEntityResponse Response(EntityMetadata metadata) =>
        new() { Results = new ParameterCollection { ["EntityMetadata"] = metadata } };

    internal static void Configure(Mock<IPooledClient> client, Func<Guid, Entity> snapshot, Action<Entity> written)
    {
        client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveEntityRequest), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(Metadata()));
        client.Setup(c => c.RetrieveAsync(Table, It.IsAny<Guid>(), It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid id, ColumnSet _, CancellationToken _) => snapshot(id));
        client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is UpdateRequest), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((request, _) => written(((UpdateRequest)request).Target))
            .ReturnsAsync(new UpdateResponse());
    }
}
