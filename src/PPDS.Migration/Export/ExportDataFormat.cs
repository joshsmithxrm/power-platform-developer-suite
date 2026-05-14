namespace PPDS.Migration.Export
{
    /// <summary>
    /// Serialization format for exported migration data.
    /// </summary>
    public enum ExportDataFormat
    {
        /// <summary>
        /// Configuration Migration Tool (CMT) ZIP archive: data.xml + data_schema.xml.
        /// Drop-in compatible with Microsoft's existing CMT tooling.
        /// </summary>
        Cmt,

        /// <summary>
        /// PPDS-native JSON document. Single file. Modern tooling (jq, JSON Schema, APIs).
        /// </summary>
        Json
    }
}
