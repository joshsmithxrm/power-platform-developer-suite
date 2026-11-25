using System;
using System.Collections.Generic;
using System.Threading;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Thrown when an import is refused before any record is written, because it could not be
    /// applied exactly as asked. No record has been written when this is thrown. Plug-in steps the
    /// import disabled before the refusal have been re-enabled where that succeeded;
    /// <see cref="PluginStepsLeftDisabled"/> lists any that could not be and remain disabled.
    /// </summary>
    public class ImportRefusedException : Exception
    {
        /// <summary>
        /// Gets the reason for the refusal, one of <see cref="ImportRefusalReasons"/>.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Gets the plug-in steps that the import disabled and could not re-enable before refusing;
        /// they remain disabled in the target. Empty when none.
        /// </summary>
        public IReadOnlyList<PluginStepInfo> PluginStepsLeftDisabled { get; init; } = Array.Empty<PluginStepInfo>();

        /// <summary>
        /// Gets the plug-in step that could not be disabled and whose re-enable also failed: it may or
        /// may not be disabled, so its state must be checked in the target. Null when none.
        /// </summary>
        public PluginStepInfo? PluginStepInUnknownState { get; init; }

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
    /// Thrown when an import is cancelled and one or more plug-in steps it disabled could not be
    /// re-enabled. A cancelled import returns no result, so the steps left disabled travel with the
    /// cancellation. It is an <see cref="OperationCanceledException"/>.
    /// </summary>
    public class ImportCancelledException : OperationCanceledException
    {
        /// <summary>
        /// Gets the plug-in steps that remain disabled in the target.
        /// </summary>
        public IReadOnlyList<PluginStepInfo> PluginStepsLeftDisabled { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImportCancelledException"/> class.
        /// </summary>
        /// <param name="pluginStepsLeftDisabled">The steps that remain disabled.</param>
        /// <param name="cancellation">The cancellation that stopped the import.</param>
        public ImportCancelledException(IReadOnlyList<PluginStepInfo> pluginStepsLeftDisabled, OperationCanceledException cancellation)
            : base($"The import was cancelled and {pluginStepsLeftDisabled?.Count ?? 0} plug-in step(s) it disabled could not be re-enabled.",
                   cancellation, cancellation?.CancellationToken ?? CancellationToken.None)
        {
            PluginStepsLeftDisabled = pluginStepsLeftDisabled ?? Array.Empty<PluginStepInfo>();
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
        /// The metadata of a table whose plug-in steps are to be disabled could not be read from the
        /// target environment for another reason than the table not existing (throttling, sign-in, a
        /// missing privilege, an outage).
        /// </summary>
        public const string PluginTableMetadataUnavailable = "Import.PluginTableMetadataUnavailable";

        /// <summary>
        /// The import mode is not one of <see cref="ImportMode"/>'s members.
        /// </summary>
        public const string ModeNotImplemented = "Import.ModeNotImplemented";

        /// <summary>
        /// The schema carries an import-only attribute whose value the import cannot apply exactly
        /// (see <see cref="Models.MigrationSchema.ImportIssues"/>).
        /// </summary>
        public const string SchemaNotApplicable = "Import.SchemaNotApplicable";

        /// <summary>
        /// A plug-in step that was to be disabled for the import could not be disabled.
        /// </summary>
        public const string PluginStepNotDisabled = "Import.PluginStepNotDisabled";
    }
}
