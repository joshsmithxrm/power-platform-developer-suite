using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class FileColumnTransferHelperTests
{
    private const int FourMB = 4_194_304;

    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly FileColumnTransferHelper _sut;

    public FileColumnTransferHelperTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();

        _pool.Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _sut = new FileColumnTransferHelper(_pool.Object);
    }

    #region UploadAsync - chunked upload (AC-35)

    [Fact]
    public async Task UploadsInFourMegabyteChunks()
    {
        // Arrange: 10MB data -> expect 3 UploadBlock calls (4MB + 4MB + 2MB)
        var tenMB = 10 * 1024 * 1024; // 10,485,760 bytes
        var data = new byte[tenMB];
        new Random(42).NextBytes(data);

        var recordId = Guid.NewGuid();
        const string continuationToken = "test-continuation-token";

        // Mock InitializeFileBlocksUpload
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "InitializeFileBlocksUpload"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new OrganizationResponse();
                response["FileContinuationToken"] = continuationToken;
                return response;
            });

        // Track UploadBlock calls
        var uploadCalls = new List<(byte[] BlockData, string BlockId)>();
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "UploadBlock"),
                It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                var blockData = (byte[])req["BlockData"];
                var blockId = (string)req["BlockId"];
                var copy = new byte[blockData.Length];
                Array.Copy(blockData, copy, blockData.Length);
                uploadCalls.Add((copy, blockId));
            })
            .ReturnsAsync(new OrganizationResponse());

        // Mock CommitFileBlocksUpload
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "CommitFileBlocksUpload"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        await _sut.UploadAsync("account", recordId, "myfilecolumn",
            data, "report.pdf", "application/pdf", CancellationToken.None);

        // Assert: exactly 3 UploadBlock calls
        uploadCalls.Should().HaveCount(3);

        // First chunk: 4MB
        uploadCalls[0].BlockData.Should().HaveCount(FourMB);
        // Second chunk: 4MB
        uploadCalls[1].BlockData.Should().HaveCount(FourMB);
        // Third chunk: remainder (10MB - 8MB = 2,097,152 bytes)
        uploadCalls[2].BlockData.Should().HaveCount(tenMB - 2 * FourMB);

        // Verify init was called with correct parameters
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "InitializeFileBlocksUpload"
                && ((EntityReference)r["Target"]).LogicalName == "account"
                && ((EntityReference)r["Target"]).Id == recordId
                && (string)r["FileAttributeName"] == "myfilecolumn"
                && (string)r["FileName"] == "report.pdf"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify commit was called with correct parameters
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "CommitFileBlocksUpload"
                && (string)r["FileName"] == "report.pdf"
                && (string)r["MimeType"] == "application/pdf"
                && (string)r["FileContinuationToken"] == continuationToken
                && ((string[])r["BlockIds"]).Length == 3),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify the uploaded data matches the original
        var reassembled = uploadCalls.SelectMany(c => c.BlockData).ToArray();
        reassembled.Should().Equal(data);
    }

    #endregion

    #region DownloadAsync - chunked download

    [Fact]
    public async Task DownloadReturnsCompleteData()
    {
        // Arrange: simulate a 6MB file downloaded in 2 chunks (4MB + 2MB)
        var totalSize = 6 * 1024 * 1024L; // 6,291,456 bytes
        var fullData = new byte[totalSize];
        new Random(99).NextBytes(fullData);

        var chunk1 = new byte[FourMB];
        Array.Copy(fullData, 0, chunk1, 0, FourMB);
        var chunk2 = new byte[totalSize - FourMB];
        Array.Copy(fullData, FourMB, chunk2, 0, (int)(totalSize - FourMB));

        var recordId = Guid.NewGuid();
        const string continuationToken = "download-token";

        // Mock InitializeFileBlocksDownload
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "InitializeFileBlocksDownload"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new OrganizationResponse();
                response["FileContinuationToken"] = continuationToken;
                response["FileSizeInBytes"] = totalSize;
                return response;
            });

        // Mock DownloadBlock - return appropriate chunk based on offset
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r =>
                    r.RequestName == "DownloadBlock"
                    && (long)r["Offset"] == 0L),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new OrganizationResponse();
                response["Data"] = chunk1;
                return response;
            });

        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r =>
                    r.RequestName == "DownloadBlock"
                    && (long)r["Offset"] == (long)FourMB),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new OrganizationResponse();
                response["Data"] = chunk2;
                return response;
            });

        // Act
        var result = await _sut.DownloadAsync("account", recordId, "myfilecolumn",
            CancellationToken.None);

        // Assert: concatenated result matches original data
        result.Should().Equal(fullData);
        result.Should().HaveCount((int)totalSize);

        // Verify init was called correctly
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "InitializeFileBlocksDownload"
                && ((EntityReference)r["Target"]).LogicalName == "account"
                && ((EntityReference)r["Target"]).Id == recordId
                && (string)r["FileAttributeName"] == "myfilecolumn"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify two download block requests
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "DownloadBlock"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #endregion

    #region UploadAsync - small file single chunk

    [Fact]
    public async Task SmallFileUploadsInSingleChunk()
    {
        // Arrange: 1MB data -> expect single UploadBlock call
        var oneMB = 1 * 1024 * 1024;
        var data = new byte[oneMB];
        new Random(7).NextBytes(data);

        var recordId = Guid.NewGuid();
        const string continuationToken = "small-file-token";

        // Mock InitializeFileBlocksUpload
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "InitializeFileBlocksUpload"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new OrganizationResponse();
                response["FileContinuationToken"] = continuationToken;
                return response;
            });

        // Mock UploadBlock
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "UploadBlock"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        // Mock CommitFileBlocksUpload
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "CommitFileBlocksUpload"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        await _sut.UploadAsync("contact", recordId, "attachmentfield",
            data, "small.txt", "text/plain", CancellationToken.None);

        // Assert: exactly 1 UploadBlock call
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "UploadBlock"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify the single block contains the full data
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "UploadBlock"
                && ((byte[])r["BlockData"]).Length == oneMB),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify commit was called with single block ID
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "CommitFileBlocksUpload"
                && ((string[])r["BlockIds"]).Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new FileColumnTransferHelper(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    #endregion
}
