using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Models;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Interface for writing CMT-compatible schema files.
    /// </summary>
    public interface ICmtSchemaWriter
    {
        /// <summary>
        /// Writes a migration schema to a file.
        /// </summary>
        /// <param name="schema">The schema to write.</param>
        /// <param name="path">The output file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task WriteAsync(MigrationSchema schema, string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes a migration schema to a stream.
        /// </summary>
        /// <param name="schema">The schema to write.</param>
        /// <param name="stream">The output stream.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task WriteAsync(MigrationSchema schema, Stream stream, CancellationToken cancellationToken = default);
    }
}
