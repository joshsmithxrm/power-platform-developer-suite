using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Formats;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Reads only the schema of a data archive or a schema file, to find what an import would refuse
    /// in its import-only attributes before anything connects (#1934 r2 should-fix 4). Reads local
    /// files only.
    /// </summary>
    public static class ImportSchemaCheck
    {
        /// <summary>
        /// Returns the import issues of the schema inside a CMT data archive (empty when none, or when
        /// the archive has no schema: the import reports that itself).
        /// </summary>
        /// <param name="dataPath">The data archive.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<IReadOnlyList<string>> ReadArchiveImportIssuesAsync(string dataPath, CancellationToken cancellationToken = default)
        {
            using var archive = ZipFile.OpenRead(dataPath);
            var entry = archive.GetEntry("data_schema.xml") ?? archive.GetEntry("schema.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            var schema = await new CmtSchemaReader().ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return schema.ImportIssues;
        }

        /// <summary>
        /// Returns the import issues of a schema file (empty when none).
        /// </summary>
        /// <param name="schemaPath">The schema file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<IReadOnlyList<string>> ReadSchemaFileImportIssuesAsync(string schemaPath, CancellationToken cancellationToken = default)
        {
            var schema = await new CmtSchemaReader().ReadAsync(schemaPath, cancellationToken).ConfigureAwait(false);
            return schema.ImportIssues;
        }
    }
}
