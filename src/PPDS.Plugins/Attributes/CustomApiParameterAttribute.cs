using System;

namespace PPDS.Plugins
{
    /// <summary>
    /// Defines a request or response parameter for a Custom API.
    /// Apply multiple times to a plugin class to define all input and output parameters.
    /// Must be used together with <see cref="CustomApiAttribute"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// [CustomApi(UniqueName = "ppds_ProcessOrder", DisplayName = "Process Order")]
    /// [CustomApiParameter(Name = "OrderId", Type = ApiParameterType.Guid, Direction = ParameterDirection.Input)]
    /// [CustomApiParameter(Name = "Notes", Type = ApiParameterType.String, Direction = ParameterDirection.Input, IsOptional = true)]
    /// [CustomApiParameter(Name = "ConfirmationNumber", Type = ApiParameterType.String, Direction = ParameterDirection.Output)]
    /// public class ProcessOrderPlugin : PluginBase
    /// {
    ///     // Plugin implementation
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class CustomApiParameterAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the logical name of the parameter used in code.
        /// Required.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique name of the parameter including the publisher prefix.
        /// If not specified, the Name is used.
        /// </summary>
        public string? UniqueName { get; set; }

        /// <summary>
        /// Gets or sets the display name of the parameter shown in the Dataverse UI.
        /// If not specified, the Name is used.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// Gets or sets a description of the parameter.
        /// Stored as metadata in Dataverse.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the data type of the parameter.
        /// Required.
        /// </summary>
        public ApiParameterType Type { get; set; }

        /// <summary>
        /// Gets or sets the logical name of the entity for EntityReference parameters.
        /// Required when Type is EntityReference, Entity, or EntityCollection.
        /// </summary>
        public string? LogicalEntityName { get; set; }

        /// <summary>
        /// Gets or sets whether the parameter is optional.
        /// Optional parameters do not need to be provided when invoking the API.
        /// Default: false (required).
        /// </summary>
        public bool IsOptional { get; set; }

        /// <summary>
        /// Gets or sets whether this is an input (request) or output (response) parameter.
        /// Default: Input.
        /// </summary>
        public ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        /// <summary>
        /// Initializes a new instance of the CustomApiParameterAttribute class.
        /// </summary>
        public CustomApiParameterAttribute()
        {
        }
    }
}
