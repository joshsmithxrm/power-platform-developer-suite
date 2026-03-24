using System;

namespace PPDS.Plugins
{
    /// <summary>
    /// Defines a pre-image or post-image for a plugin step.
    /// Images provide access to entity data before or after the operation.
    /// </summary>
    /// <example>
    /// <code>
    /// [PluginStep(
    ///     Message = "Update",
    ///     EntityLogicalName = "account",
    ///     Stage = PluginStage.PostOperation)]
    /// [PluginImage(
    ///     ImageType = PluginImageType.PreImage,
    ///     Name = "PreImage",
    ///     Attributes = "name,telephone1,revenue")]
    /// public class AccountAuditPlugin : PluginBase
    /// {
    ///     // Access via context.PreEntityImages["PreImage"]
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class PluginImageAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the type of image (PreImage, PostImage, or Both).
        /// Required.
        /// </summary>
        public PluginImageType ImageType { get; set; }

        /// <summary>
        /// Gets or sets the name used to access the image in the plugin context.
        /// This is the key used in PreEntityImages or PostEntityImages collections.
        /// Required.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the comma-separated list of attributes to include in the image.
        /// If empty, all attributes are included (not recommended for performance).
        /// </summary>
        public string? Attributes { get; set; }

        /// <summary>
        /// Gets or sets the entity alias for the image.
        /// Defaults to the Name if not specified.
        /// </summary>
        public string? EntityAlias { get; set; }

        /// <summary>
        /// Gets or sets the StepId to associate this image with a specific step.
        /// Only needed when the plugin has multiple steps registered.
        /// Must match the StepId property of a PluginStepAttribute on the same class.
        /// </summary>
        public string? StepId { get; set; }

        /// <summary>
        /// Gets or sets a description of this image's purpose.
        /// Stored as metadata in Dataverse.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the message property name that carries the entity.
        /// If null, auto-inferred from the message name (e.g., "Target" for Create/Update).
        /// Override for non-standard messages.
        /// </summary>
        public string? MessagePropertyName { get; set; }

        /// <summary>
        /// Initializes a new instance of the PluginImageAttribute class.
        /// </summary>
        public PluginImageAttribute()
        {
        }

        /// <summary>
        /// Initializes a new instance of the PluginImageAttribute class with required parameters.
        /// </summary>
        /// <param name="imageType">The type of image</param>
        /// <param name="name">The name to access the image in code</param>
        public PluginImageAttribute(PluginImageType imageType, string name)
        {
            ImageType = imageType;
            Name = name;
        }

        /// <summary>
        /// Initializes a new instance of the PluginImageAttribute class with all common parameters.
        /// </summary>
        /// <param name="imageType">The type of image</param>
        /// <param name="name">The name to access the image in code</param>
        /// <param name="attributes">Comma-separated attributes to include</param>
        public PluginImageAttribute(PluginImageType imageType, string name, string attributes)
        {
            ImageType = imageType;
            Name = name;
            Attributes = attributes;
        }
    }
}
