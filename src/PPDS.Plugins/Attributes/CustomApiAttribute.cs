using System;

namespace PPDS.Plugins
{
    /// <summary>
    /// Defines Custom API registration configuration.
    /// Apply to plugin classes to specify how the Custom API should be registered in Dataverse.
    /// Only one Custom API can be defined per class.
    /// </summary>
    /// <example>
    /// <code>
    /// [CustomApi(
    ///     UniqueName = "ppds_ProcessOrder",
    ///     DisplayName = "Process Order",
    ///     Description = "Processes an order and returns a confirmation number")]
    /// [CustomApiParameter(Name = "OrderId", Type = ApiParameterType.Guid, Direction = ParameterDirection.Input)]
    /// [CustomApiParameter(Name = "ConfirmationNumber", Type = ApiParameterType.String, Direction = ParameterDirection.Output)]
    /// public class ProcessOrderPlugin : PluginBase
    /// {
    ///     // Plugin implementation
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CustomApiAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the unique name of the Custom API.
        /// Must include a publisher prefix (e.g., "ppds_MyCustomApi").
        /// Required.
        /// </summary>
        public string UniqueName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name of the Custom API shown in the Dataverse UI.
        /// Required.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logical name of the Custom API.
        /// If not specified, the UniqueName is used.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets a description of what this Custom API does.
        /// Stored as metadata in Dataverse and displayed in the UI.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the binding type that determines whether the API is global,
        /// entity-bound, or entity-collection-bound.
        /// Default: Global.
        /// </summary>
        public ApiBindingType BindingType { get; set; } = ApiBindingType.Global;

        /// <summary>
        /// Gets or sets the logical name of the entity this API is bound to.
        /// Only applicable when BindingType is Entity or EntityCollection.
        /// </summary>
        public string? BoundEntity { get; set; }

        /// <summary>
        /// Gets or sets whether this Custom API is a function (returns a value without side effects)
        /// or an action (may have side effects).
        /// Default: false (action).
        /// </summary>
        public bool IsFunction { get; set; }

        /// <summary>
        /// Gets or sets whether this Custom API is private.
        /// Private APIs are not visible in the Web API $metadata document.
        /// Default: false.
        /// </summary>
        public bool IsPrivate { get; set; }

        /// <summary>
        /// Gets or sets the name of the privilege required to execute this Custom API.
        /// If specified, only users with this privilege can invoke the API.
        /// </summary>
        public string? ExecutePrivilegeName { get; set; }

        /// <summary>
        /// Gets or sets the type of processing steps allowed for this Custom API.
        /// Controls whether synchronous and/or asynchronous plug-in steps can be registered.
        /// Default: None.
        /// </summary>
        public ApiProcessingStepType AllowedProcessingStepType { get; set; } = ApiProcessingStepType.None;

        /// <summary>
        /// Initializes a new instance of the CustomApiAttribute class.
        /// </summary>
        public CustomApiAttribute()
        {
        }
    }
}
