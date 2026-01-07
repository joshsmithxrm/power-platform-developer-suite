using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Extension methods for applying import options to OrganizationRequest objects.
    /// </summary>
    internal static class OrganizationRequestExtensions
    {
        /// <summary>
        /// Applies bypass options to an OrganizationRequest for individual operations.
        /// </summary>
        /// <remarks>
        /// This mirrors the logic in BulkOperationExecutor.ApplyBypassOptions
        /// to ensure consistent bypass behavior between bulk and individual operations.
        /// </remarks>
        /// <param name="request">The request to apply options to.</param>
        /// <param name="options">The import options containing bypass settings.</param>
        internal static void ApplyBypassOptions(this OrganizationRequest request, ImportOptions options)
        {
            // Custom business logic bypass
            if (options.BypassCustomPlugins != CustomLogicBypass.None)
            {
                var parts = new List<string>(2);
                if (options.BypassCustomPlugins.HasFlag(CustomLogicBypass.Synchronous))
                    parts.Add("CustomSync");
                if (options.BypassCustomPlugins.HasFlag(CustomLogicBypass.Asynchronous))
                    parts.Add("CustomAsync");
                request.Parameters["BypassBusinessLogicExecution"] = string.Join(",", parts);
            }

            // Power Automate flows bypass
            if (options.BypassPowerAutomateFlows)
            {
                request.Parameters["SuppressCallbackRegistrationExpanderJob"] = true;
            }
        }
    }
}
