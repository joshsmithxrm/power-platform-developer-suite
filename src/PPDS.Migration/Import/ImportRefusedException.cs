using System;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Thrown when an import is refused before any record is written, because it could not be
    /// applied exactly as asked. Nothing in the target has been changed when this is thrown
    /// (plug-in steps disabled before a refusal are re-enabled first).
    /// </summary>
    public class ImportRefusedException : Exception
    {
        /// <summary>
        /// Gets the reason for the refusal, one of <see cref="ImportRefusalReasons"/>.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImportRefusedException"/> class.
        /// </summary>
        /// <param name="reason">One of <see cref="ImportRefusalReasons"/>.</param>
        /// <param name="message">What was refused and what to do instead.</param>
        /// <param name="innerException">The underlying error, if any.</param>
        public ImportRefusedException(string reason, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        }
    }

    /// <summary>
    /// Reasons for an <see cref="ImportRefusedException"/>.
    /// </summary>
    public static class ImportRefusalReasons
    {
        /// <summary>
        /// <see cref="ImportMode.Skip"/> was given as the mode for the whole import. Skip is a
        /// per-table mode (schema attribute <c>importMode="skip"</c>).
        /// </summary>
        public const string SkipIsPerTable = "Import.SkipIsPerTable";

        /// <summary>
        /// A table whose plug-in steps are to be disabled (<c>disableplugins="true"</c>) could not be
        /// resolved in the target environment by its logical name.
        /// </summary>
        public const string PluginTableNotResolved = "Import.PluginTableNotResolved";

        /// <summary>
        /// A plug-in step that was to be disabled for the import could not be disabled.
        /// </summary>
        public const string PluginStepNotDisabled = "Import.PluginStepNotDisabled";
    }
}
