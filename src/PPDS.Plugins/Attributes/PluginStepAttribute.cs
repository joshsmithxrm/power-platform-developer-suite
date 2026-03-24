using System;

namespace PPDS.Plugins
{
    /// <summary>
    /// Defines plugin step registration configuration.
    /// Apply to plugin classes to specify how the plugin should be registered in Dataverse.
    /// Multiple attributes can be applied for plugins that handle multiple messages/entities.
    /// </summary>
    /// <example>
    /// <code>
    /// [PluginStep(
    ///     Message = "Update",
    ///     EntityLogicalName = "account",
    ///     Stage = PluginStage.PostOperation,
    ///     Mode = PluginMode.Asynchronous,
    ///     FilteringAttributes = "name,telephone1")]
    /// public class AccountUpdatePlugin : PluginBase
    /// {
    ///     // Plugin implementation
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class PluginStepAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the SDK message name (e.g., Create, Update, Delete, Retrieve, RetrieveMultiple).
        /// Required.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logical name of the primary entity this step applies to.
        /// Use "none" for messages that don't require an entity (e.g., WhoAmI).
        /// Required.
        /// </summary>
        public string EntityLogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logical name of the secondary entity for relationship-based messages.
        /// Used with Associate, Disassociate, and SetRelated messages where two entity types are involved.
        /// </summary>
        public string? SecondaryEntityLogicalName { get; set; }

        /// <summary>
        /// Gets or sets the pipeline stage when this plugin executes.
        /// Required.
        /// </summary>
        public PluginStage Stage { get; set; }

        /// <summary>
        /// Gets or sets the execution mode (synchronous or asynchronous).
        /// Default: Synchronous.
        /// </summary>
        public PluginMode Mode { get; set; } = PluginMode.Synchronous;

        /// <summary>
        /// Gets or sets the comma-separated list of attributes that trigger this plugin.
        /// Only applicable for Update message. If empty, triggers on any attribute change.
        /// </summary>
        public string? FilteringAttributes { get; set; }

        /// <summary>
        /// Gets or sets the execution order when multiple plugins are registered for the same event.
        /// Lower numbers execute first. Must be between 1 and 999999. Default: 1.
        /// </summary>
        public int ExecutionOrder { get; set; } = 1;

        /// <summary>
        /// Gets or sets the display name for the step.
        /// If not specified, auto-generated as "{PluginTypeName}: {Message} of {Entity}".
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the unsecure configuration string passed to the plugin constructor.
        /// This configuration is stored in plain text and is visible to users with appropriate read access
        /// to the plugin step entity, as determined by their security roles and privileges in Dataverse.
        /// </summary>
        public string? UnsecureConfiguration { get; set; }

        /// <summary>
        /// Gets or sets a description of what this plugin step does.
        /// This is stored as metadata in Dataverse and helps document the step's purpose.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether to automatically delete the async job on successful completion.
        /// Only applies when Mode is Asynchronous. Default is false (keep async job records).
        /// Set to true to clean up async job records after successful execution.
        /// </summary>
        public bool AsyncAutoDelete { get; set; }

        /// <summary>
        /// Gets or sets the deployment target.
        /// Default: ServerOnly.
        /// </summary>
        public PluginDeployment Deployment { get; set; } = PluginDeployment.ServerOnly;

        /// <summary>
        /// Gets or sets the impersonation user for step execution.
        /// Values: null (calling user), "CallingUser", "System", a GUID, email, or domain name.
        /// </summary>
        public string? RunAsUser { get; set; }

        /// <summary>
        /// Gets or sets whether this step can be bypassed via BypassBusinessLogicExecution.
        /// Default: true (matches Dataverse default).
        /// </summary>
        public bool CanBeBypassed { get; set; } = true;

        /// <summary>
        /// Gets or sets whether this step can use a read-only database connection.
        /// Set to true for steps that only read data, for improved performance.
        /// Default: false.
        /// </summary>
        public bool CanUseReadOnlyConnection { get; set; }

        /// <summary>
        /// Gets or sets the pipeline invocation source.
        /// Parent executes in the parent pipeline, Child in child pipelines only.
        /// Default: Parent.
        /// </summary>
        public PluginInvocationSource InvocationSource { get; set; } = PluginInvocationSource.Parent;

        /// <summary>
        /// Gets or sets a unique identifier for this step when a plugin has multiple steps.
        /// Used to associate PluginImageAttribute with a specific step.
        /// </summary>
        public string? StepId { get; set; }

        /// <summary>
        /// Initializes a new instance of the PluginStepAttribute class.
        /// </summary>
        public PluginStepAttribute()
        {
        }

        /// <summary>
        /// Initializes a new instance of the PluginStepAttribute class with required parameters.
        /// </summary>
        /// <param name="message">The SDK message name (Create, Update, Delete, etc.)</param>
        /// <param name="entityLogicalName">The entity logical name</param>
        /// <param name="stage">The pipeline stage</param>
        public PluginStepAttribute(string message, string entityLogicalName, PluginStage stage)
        {
            Message = message;
            EntityLogicalName = entityLogicalName;
            Stage = stage;
        }
    }
}
