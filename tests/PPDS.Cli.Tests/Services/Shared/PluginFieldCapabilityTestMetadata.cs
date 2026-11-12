using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tests.Services.Shared;

// Existing write-construction tests model a target that supports this field on
// Create and Update. Separate capability tests cover create-only and unknown targets.
internal static class PluginFieldCapabilityTestMetadata
{
    internal static void AllowReadOnlyConnectionWrites(Mock<IPooledClient> client)
    {
        static RetrieveAttributeResponse Response()
        {
            var metadata = new BooleanAttributeMetadata { LogicalName = "canusereadonlyconnection" };
            foreach (var property in new[] { "IsValidForRead", "IsValidForCreate", "IsValidForUpdate" })
                typeof(AttributeMetadata).GetProperty(property)!.SetValue(metadata, true);
            return new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = metadata } };
        }
        client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveAttributeRequest &&
                ((RetrieveAttributeRequest)r).EntityLogicalName == "sdkmessageprocessingstep" &&
                ((RetrieveAttributeRequest)r).LogicalName == "canusereadonlyconnection"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response);
    }
}
