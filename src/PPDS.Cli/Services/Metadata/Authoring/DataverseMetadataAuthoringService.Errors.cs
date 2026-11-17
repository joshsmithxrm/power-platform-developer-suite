using System.Globalization;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Services.Metadata.Authoring;

public partial class DataverseMetadataAuthoringService
{
    private static string AppendDataverseFaultCode(string message, Exception exception)
    {
        // Read only the typed integer: server messages, traces and arbitrary error
        // details are not safe for the shared CLI/MCP user-message boundary.
        if (FindTypedFault(exception) is { Detail: not null } fault)
        {
            var code = unchecked((uint)fault.Detail.ErrorCode).ToString("x8", CultureInfo.InvariantCulture);
            return $"{message} Dataverse fault 0x{code}. Server diagnostic text is withheld.";
        }

        return message;
    }

    /// <summary>The nearest typed Dataverse fault carrying a detail, within a bounded walk of the inner-exception chain.</summary>
    private static FaultException<OrganizationServiceFault>? FindTypedFault(Exception exception)
    {
        Exception? current = exception;
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.InnerException)
        {
            if (current is FaultException<OrganizationServiceFault> { Detail: not null } fault)
                return fault;
        }

        return null;
    }
}
