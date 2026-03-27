using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Pooling;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Handles chunked upload/download of file column binary data.
    /// Uses 4MB (4,194,304 byte) chunks per Dataverse SDK limit.
    /// </summary>
    public class FileColumnTransferHelper
    {
        private const int ChunkSize = 4_194_304; // 4MB

        private readonly IDataverseConnectionPool _connectionPool;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileColumnTransferHelper"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        public FileColumnTransferHelper(IDataverseConnectionPool connectionPool)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        }

        /// <summary>
        /// Downloads file column data in chunks.
        /// </summary>
        /// <param name="entityName">The logical name of the entity.</param>
        /// <param name="recordId">The record identifier.</param>
        /// <param name="fieldName">The file column attribute name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The complete file data as a byte array.</returns>
        public async Task<byte[]> DownloadAsync(
            string entityName, Guid recordId, string fieldName,
            CancellationToken cancellationToken)
        {
            // D2 exception: FileContinuationToken is session-bound; must use same client for all chunks.
            await using var client = await _connectionPool.GetClientAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Step 1: Initialize the download
            var initRequest = new OrganizationRequest("InitializeFileBlocksDownload")
            {
                ["Target"] = new EntityReference(entityName, recordId),
                ["FileAttributeName"] = fieldName
            };

            var initResponse = await client.ExecuteAsync(initRequest, cancellationToken).ConfigureAwait(false);
            var fileContinuationToken = (string)initResponse["FileContinuationToken"];
            var fileSizeInBytes = (long)initResponse["FileSizeInBytes"];

            // Step 2: Download all blocks
            var allData = new List<byte>((int)Math.Min(fileSizeInBytes, int.MaxValue));
            long offset = 0;

            while (offset < fileSizeInBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blockLength = (int)Math.Min(ChunkSize, fileSizeInBytes - offset);

                var downloadRequest = new OrganizationRequest("DownloadBlock")
                {
                    ["FileContinuationToken"] = fileContinuationToken,
                    ["Offset"] = offset,
                    ["BlockLength"] = blockLength
                };

                var downloadResponse = await client.ExecuteAsync(downloadRequest, cancellationToken).ConfigureAwait(false);
                var blockData = (byte[])downloadResponse["Data"];
                allData.AddRange(blockData);

                offset += blockData.Length;
            }

            return allData.ToArray();
        }

        /// <summary>
        /// Uploads file column data in 4MB chunks. AC-35.
        /// Uses InitializeFileBlocksUploadRequest -> UploadBlockRequest -> CommitFileBlocksUploadRequest.
        /// </summary>
        /// <param name="entityName">The logical name of the entity.</param>
        /// <param name="recordId">The record identifier.</param>
        /// <param name="fieldName">The file column attribute name.</param>
        /// <param name="data">The file data to upload.</param>
        /// <param name="fileName">The original file name.</param>
        /// <param name="mimeType">The MIME type of the file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task UploadAsync(
            string entityName, Guid recordId, string fieldName,
            byte[] data, string fileName, string mimeType,
            CancellationToken cancellationToken)
        {
            // D2 exception: FileContinuationToken is session-bound; must use same client for all chunks.
            await using var client = await _connectionPool.GetClientAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Step 1: Initialize the upload
            var initRequest = new OrganizationRequest("InitializeFileBlocksUpload")
            {
                ["Target"] = new EntityReference(entityName, recordId),
                ["FileAttributeName"] = fieldName,
                ["FileName"] = fileName
            };

            var initResponse = await client.ExecuteAsync(initRequest, cancellationToken).ConfigureAwait(false);
            var fileContinuationToken = (string)initResponse["FileContinuationToken"];

            // Step 2: Upload chunks
            var blockIds = new List<string>();
            var offset = 0;

            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blockLength = Math.Min(ChunkSize, data.Length - offset);
                var blockData = new byte[blockLength];
                Array.Copy(data, offset, blockData, 0, blockLength);

                var blockId = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(blockIds.Count.ToString("D6")));

                var uploadRequest = new OrganizationRequest("UploadBlock")
                {
                    ["FileContinuationToken"] = fileContinuationToken,
                    ["BlockData"] = blockData,
                    ["BlockId"] = blockId
                };

                await client.ExecuteAsync(uploadRequest, cancellationToken).ConfigureAwait(false);
                blockIds.Add(blockId);

                offset += blockLength;
            }

            // Step 3: Commit the upload
            var commitRequest = new OrganizationRequest("CommitFileBlocksUpload")
            {
                ["FileName"] = fileName,
                ["MimeType"] = mimeType,
                ["FileContinuationToken"] = fileContinuationToken,
                ["BlockIds"] = blockIds.ToArray()
            };

            await client.ExecuteAsync(commitRequest, cancellationToken).ConfigureAwait(false);
        }
    }
}
