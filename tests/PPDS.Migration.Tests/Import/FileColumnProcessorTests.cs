using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class FileColumnProcessorTests
{
    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly FileColumnTransferHelper _transferHelper;

    public FileColumnProcessorTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();

        _pool.Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _transferHelper = new FileColumnTransferHelper(_pool.Object);
    }

    #region ProcessAsync — skip when no file data

    [Fact]
    public async Task ProcessAsync_SkipsWhenNoFileData()
    {
        // Arrange
        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(
            fileData: new Dictionary<string, IReadOnlyList<FileColumnData>>());

        // Act
        var result = await processor.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(0);
        result.SuccessCount.Should().Be(0);
    }

    #endregion

    #region ProcessAsync — upload with mapped record IDs

    [Fact]
    public async Task ProcessAsync_UploadsFileDataWithMappedRecordIds()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        SetupMockForUpload();

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = sourceId,
                        FieldName = "cr_document",
                        FileName = "report.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1, 2, 3, 4 }
                    }
                }
            }
        };

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceId, targetId);

        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData, idMappings: idMappings);

        // Act
        var result = await processor.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);

        // Verify the upload was called with the TARGET record ID (mapped), not the source
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r =>
                r.RequestName == "InitializeFileBlocksUpload" &&
                ((EntityReference)r["Target"]).Id == targetId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region ProcessAsync — progress reporting

    [Fact]
    public async Task ProcessAsync_ReportsProgressPerFile()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        SetupMockForUpload();

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = sourceId,
                        FieldName = "cr_document",
                        FileName = "test.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1, 2, 3 }
                    }
                }
            }
        };

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceId, targetId);

        var mockProgress = new Mock<IProgressReporter>();
        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData, idMappings: idMappings, progress: mockProgress.Object);

        // Act
        await processor.ProcessAsync(context, CancellationToken.None);

        // Assert — progress was reported
        mockProgress.Verify(p => p.Report(It.Is<ProgressEventArgs>(e =>
            e.Phase == MigrationPhase.Importing)),
            Times.AtLeastOnce);
    }

    #endregion

    #region ProcessAsync — continue on error

    [Fact]
    public async Task ProcessAsync_ContinuesOnErrorWhenOptionSet()
    {
        // Arrange — first upload fails, second succeeds
        var sourceId1 = Guid.NewGuid();
        var sourceId2 = Guid.NewGuid();
        var targetId1 = Guid.NewGuid();
        var targetId2 = Guid.NewGuid();

        var callCount = 0;
        var initResponse = new OrganizationResponse();
        initResponse["FileContinuationToken"] = "test-token";

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "InitializeFileBlocksUpload"),
            It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, ct) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("Upload failed");
                return Task.FromResult(initResponse);
            });

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "UploadBlock"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "CommitFileBlocksUpload"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = sourceId1,
                        FieldName = "doc1",
                        FileName = "a.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1 }
                    },
                    new FileColumnData
                    {
                        RecordId = sourceId2,
                        FieldName = "doc2",
                        FileName = "b.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 2 }
                    }
                }
            }
        };

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceId1, targetId1);
        idMappings.AddMapping("account", sourceId2, targetId2);

        var options = new ImportOptions { ContinueOnError = true };
        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData, idMappings: idMappings, options: options);

        // Act
        var result = await processor.ProcessAsync(context, CancellationToken.None);

        // Assert — continued past the first failure
        result.Success.Should().BeFalse();
        result.FailureCount.Should().Be(1);
        result.SuccessCount.Should().Be(1);
        result.Errors.Should().HaveCount(1);
    }

    #endregion

    #region ProcessAsync — missing ID mapping

    [Fact]
    public async Task ProcessAsync_NoMappingForRecord_RecordsError()
    {
        // Arrange — no ID mapping exists for the source record
        var sourceId = Guid.NewGuid();

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = sourceId,
                        FieldName = "cr_document",
                        FileName = "test.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1, 2, 3 }
                    }
                }
            }
        };

        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData);
        // Note: NOT adding any ID mapping

        // Act
        var result = await processor.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureCount.Should().Be(1);
        result.SuccessCount.Should().Be(0);
        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("mapping");
    }

    #endregion

    #region ProcessAsync — multiple entities

    [Fact]
    public async Task ProcessAsync_HandlesMultipleEntities()
    {
        // Arrange — file data for two different entities
        var accountSourceId = Guid.NewGuid();
        var accountTargetId = Guid.NewGuid();
        var contactSourceId = Guid.NewGuid();
        var contactTargetId = Guid.NewGuid();

        SetupMockForUpload();

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = accountSourceId,
                        FieldName = "cr_doc",
                        FileName = "account.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1, 2 }
                    }
                }
            },
            {
                "contact", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = contactSourceId,
                        FieldName = "cr_photo",
                        FileName = "photo.jpg",
                        MimeType = "image/jpeg",
                        Data = new byte[] { 3, 4 }
                    }
                }
            }
        };

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", accountSourceId, accountTargetId);
        idMappings.AddMapping("contact", contactSourceId, contactTargetId);

        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData, idMappings: idMappings);

        // Act
        var result = await processor.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(0);
    }

    #endregion

    #region PhaseName

    [Fact]
    public void PhaseName_ReturnsExpectedValue()
    {
        var processor = new FileColumnProcessor(_transferHelper);
        processor.PhaseName.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region ProcessAsync — cancellation

    [Fact]
    public async Task ProcessAsync_RespectsCancellation()
    {
        // Arrange
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
        {
            {
                "account", new List<FileColumnData>
                {
                    new FileColumnData
                    {
                        RecordId = sourceId,
                        FieldName = "cr_document",
                        FileName = "test.pdf",
                        MimeType = "application/pdf",
                        Data = new byte[] { 1 }
                    }
                }
            }
        };

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceId, targetId);

        var processor = new FileColumnProcessor(_transferHelper);
        var context = CreateContext(fileData: fileData, idMappings: idMappings);

        // Act & Assert — should throw or return quickly due to cancellation
        var act = () => processor.ProcessAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Helpers

    private void SetupMockForUpload()
    {
        var initResponse = new OrganizationResponse();
        initResponse["FileContinuationToken"] = "test-token";

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "InitializeFileBlocksUpload"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(initResponse);

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "UploadBlock"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        _client.Setup(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "CommitFileBlocksUpload"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());
    }

    private static ImportContext CreateContext(
        IReadOnlyDictionary<string, IReadOnlyList<FileColumnData>>? fileData = null,
        IdMappingCollection? idMappings = null,
        IProgressReporter? progress = null,
        ImportOptions? options = null)
    {
        var data = new MigrationData
        {
            FileData = fileData ?? new Dictionary<string, IReadOnlyList<FileColumnData>>()
        };

        var plan = new ExecutionPlan
        {
            Tiers = new List<ImportTier>
            {
                new ImportTier { TierNumber = 0, Entities = new List<string> { "account" } }
            }
        };

        options ??= new ImportOptions { ContinueOnError = true };
        var fieldMetadata = new FieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>());

        return new ImportContext(
            data,
            plan,
            options,
            idMappings ?? new IdMappingCollection(),
            fieldMetadata,
            progress);
    }

    #endregion
}
