using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Writes migration data to a PPDS-native JSON document.
    /// </summary>
    public interface IJsonDataWriter
    {
        /// <summary>
        /// Writes migration data to a JSON file at the given path.
        /// </summary>
        Task WriteAsync(MigrationData data, string path, IProgressReporter? progress = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes migration data to a stream as a JSON document.
        /// </summary>
        Task WriteAsync(MigrationData data, Stream stream, IProgressReporter? progress = null, CancellationToken cancellationToken = default);
    }
}
