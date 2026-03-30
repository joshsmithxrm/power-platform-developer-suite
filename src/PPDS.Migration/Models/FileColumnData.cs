using System;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Represents file column binary data for a record field.
    /// </summary>
    public class FileColumnData
    {
        /// <summary>
        /// Gets or sets the record identifier.
        /// </summary>
        public Guid RecordId { get; set; }

        /// <summary>
        /// Gets or sets the file column attribute name.
        /// </summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the original file name.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        public string MimeType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the file binary data.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
