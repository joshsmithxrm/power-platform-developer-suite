using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class RelationshipProcessorTests
{
    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly RelationshipProcessor _sut;

    public RelationshipProcessorTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();

        // Default: pool returns mock client
        _pool.Setup(p => p.GetClientAsync(It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        // Default: parallelism of 1 to simplify test determinism
        _pool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(1);

        // Default: metadata cache returns empty metadata (no relationships)
        SetupEmptyMetadataResponse();

        _sut = new RelationshipProcessor(_pool.Object);
    }

    #region ProcessAsync — basic M2M association

    [Fact]
    public async Task ProcessAsync_AssociatesRecords_ViaIntersectEntity()
    {
        // Arrange
        var sourceOldId = Guid.NewGuid();
        var sourceNewId = Guid.NewGuid();
        var targetOldId = Guid.NewGuid();
        var targetNewId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceOldId, sourceNewId);
        idMappings.AddMapping("contact", targetOldId, targetNewId);

        var m2mData = new ManyToManyRelationshipData
        {
            RelationshipName = "account_contacts",
            SourceEntityName = "account",
            SourceId = sourceOldId,
            TargetEntityName = "contact",
            TargetIds = new List<Guid> { targetOldId }
        };

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["account"] = new List<ManyToManyRelationshipData> { m2mData }
            },
            idMappings: idMappings);

        AssociateRequest? capturedRequest = null;
        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .Callback<OrganizationRequest>(req => capturedRequest = req as AssociateRequest)
            .ReturnsAsync(new OrganizationResponse());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Target.LogicalName.Should().Be("account");
        capturedRequest.Target.Id.Should().Be(sourceNewId);
        capturedRequest.RelatedEntities.Should().HaveCount(1);
        capturedRequest.RelatedEntities[0].LogicalName.Should().Be("contact");
        capturedRequest.RelatedEntities[0].Id.Should().Be(targetNewId);
    }

    #endregion

    #region ProcessAsync — role entity special case

    [Fact]
    public async Task ProcessAsync_HandlesRoleLookup_ById()
    {
        // Arrange: role target is NOT in ID mappings, so the processor
        // falls back to querying Dataverse for the role by ID
        var sourceOldId = Guid.NewGuid();
        var sourceNewId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("systemuser", sourceOldId, sourceNewId);
        // Intentionally NOT mapping role — forces the LookupRoleByIdAsync path

        var m2mData = new ManyToManyRelationshipData
        {
            RelationshipName = "systemuserroles_association",
            SourceEntityName = "systemuser",
            SourceId = sourceOldId,
            TargetEntityName = "role",
            TargetIds = new List<Guid> { roleId }
        };

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["systemuser"] = new List<ManyToManyRelationshipData> { m2mData }
            },
            idMappings: idMappings);

        // Setup: RetrieveMultiple returns a role entity (role exists in target with same ID)
        var roleEntity = new Entity("role", roleId);
        roleEntity["name"] = "System Administrator";
        var entityCollection = new EntityCollection(new List<Entity> { roleEntity });
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>()))
            .ReturnsAsync(entityCollection);

        // Setup: Associate succeeds
        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        _client.Verify(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>()), Times.AtLeastOnce);
    }

    #endregion

    #region ProcessAsync — relationship name resolution

    [Fact]
    public async Task ProcessAsync_ResolveRelationshipName_FromIntersectEntity()
    {
        // Arrange: metadata maps intersect "teamroles" to SchemaName "teamroles_association"
        var sourceOldId = Guid.NewGuid();
        var sourceNewId = Guid.NewGuid();
        var targetOldId = Guid.NewGuid();
        var targetNewId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("team", sourceOldId, sourceNewId);
        idMappings.AddMapping("role", targetOldId, targetNewId);

        var m2mData = new ManyToManyRelationshipData
        {
            RelationshipName = "teamroles",
            SourceEntityName = "team",
            SourceId = sourceOldId,
            TargetEntityName = "role",
            TargetIds = new List<Guid> { targetOldId }
        };

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["team"] = new List<ManyToManyRelationshipData> { m2mData }
            },
            idMappings: idMappings);

        // Setup metadata response with intersect entity → SchemaName mapping
        SetupMetadataResponse("teamroles", "teamroles_association");

        AssociateRequest? capturedRequest = null;
        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .Callback<OrganizationRequest>(req => capturedRequest = req as AssociateRequest)
            .ReturnsAsync(new OrganizationResponse());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Relationship.SchemaName.Should().Be("teamroles_association");
    }

    #endregion

    #region ProcessAsync — duplicate association handling

    [Fact]
    public async Task ProcessAsync_DuplicateAssociation_IsIdempotent()
    {
        // Arrange
        var sourceOldId = Guid.NewGuid();
        var sourceNewId = Guid.NewGuid();
        var targetOldId = Guid.NewGuid();
        var targetNewId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceOldId, sourceNewId);
        idMappings.AddMapping("contact", targetOldId, targetNewId);

        var m2mData = new ManyToManyRelationshipData
        {
            RelationshipName = "account_contacts",
            SourceEntityName = "account",
            SourceId = sourceOldId,
            TargetEntityName = "contact",
            TargetIds = new List<Guid> { targetOldId }
        };

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["account"] = new List<ManyToManyRelationshipData> { m2mData }
            },
            idMappings: idMappings);

        // Simulate "Cannot insert duplicate key" error
        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .ThrowsAsync(new Exception("Cannot insert duplicate key"));

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert — duplicate is treated as idempotent success
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
    }

    #endregion

    #region ProcessAsync — metadata cache loaded once

    [Fact]
    public async Task ProcessAsync_LoadsMetadataCache_OncePerRun()
    {
        // Arrange: two separate M2M operations
        var sourceOldId1 = Guid.NewGuid();
        var sourceNewId1 = Guid.NewGuid();
        var targetOldId1 = Guid.NewGuid();
        var targetNewId1 = Guid.NewGuid();

        var sourceOldId2 = Guid.NewGuid();
        var sourceNewId2 = Guid.NewGuid();
        var targetOldId2 = Guid.NewGuid();
        var targetNewId2 = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceOldId1, sourceNewId1);
        idMappings.AddMapping("contact", targetOldId1, targetNewId1);
        idMappings.AddMapping("team", sourceOldId2, sourceNewId2);
        idMappings.AddMapping("role", targetOldId2, targetNewId2);

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["account"] = new List<ManyToManyRelationshipData>
                {
                    new ManyToManyRelationshipData
                    {
                        RelationshipName = "account_contacts",
                        SourceEntityName = "account",
                        SourceId = sourceOldId1,
                        TargetEntityName = "contact",
                        TargetIds = new List<Guid> { targetOldId1 }
                    }
                },
                ["team"] = new List<ManyToManyRelationshipData>
                {
                    new ManyToManyRelationshipData
                    {
                        RelationshipName = "teamroles_association",
                        SourceEntityName = "team",
                        SourceId = sourceOldId2,
                        TargetEntityName = "role",
                        TargetIds = new List<Guid> { targetOldId2 }
                    }
                }
            },
            idMappings: idMappings);

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert: RetrieveAllEntitiesRequest is used for metadata cache — should be called
        // exactly once (during the pre-load in ProcessAsync, before parallel processing)
        _client.Verify(c => c.ExecuteAsync(It.Is<OrganizationRequest>(
            r => r is RetrieveAllEntitiesRequest)), Times.Once);
    }

    #endregion

    #region ProcessAsync — progress reporting

    [Fact]
    public async Task ProcessAsync_ReportsProgress_AtThrottledInterval()
    {
        // Arrange: create enough associations that progress should be reported
        // ProgressReportInterval is 500, so we need >500 target IDs to see throttled behavior.
        // We'll verify that progress IS reported for the final completion report.
        var sourceOldId = Guid.NewGuid();
        var sourceNewId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", sourceOldId, sourceNewId);

        // Create 3 target IDs (small set — will get a final completion report)
        var targetIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var oldId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            targetIds.Add(oldId);
            idMappings.AddMapping("contact", oldId, newId);
        }

        var m2mData = new ManyToManyRelationshipData
        {
            RelationshipName = "account_contacts",
            SourceEntityName = "account",
            SourceId = sourceOldId,
            TargetEntityName = "contact",
            TargetIds = targetIds
        };

        var progressReporter = new Mock<IProgressReporter>();
        var progressReports = new List<ProgressEventArgs>();
        progressReporter.Setup(p => p.Report(It.IsAny<ProgressEventArgs>()))
            .Callback<ProgressEventArgs>(args => progressReports.Add(args));

        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                ["account"] = new List<ManyToManyRelationshipData> { m2mData }
            },
            idMappings: idMappings,
            progress: progressReporter.Object);

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is AssociateRequest)))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert: at least the final completion report should be present
        progressReports.Should().NotBeEmpty();
        // The final report has Entity = "M2M" and Message = "[M2M] Completed"
        progressReports.Should().Contain(p =>
            p.Phase == MigrationPhase.ProcessingRelationships &&
            p.Entity == "M2M" &&
            p.Message == "[M2M] Completed");
    }

    #endregion

    #region ProcessAsync — no relationships

    [Fact]
    public async Task ProcessAsync_NoRelationships_ReturnsSkipped()
    {
        // Arrange
        var context = CreateContext(
            relationshipData: new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(0);
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new RelationshipProcessor(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    #endregion

    #region Helpers

    private ImportContext CreateContext(
        IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationshipData>>? relationshipData = null,
        IdMappingCollection? idMappings = null,
        IProgressReporter? progress = null)
    {
        var data = new MigrationData
        {
            RelationshipData = relationshipData ?? new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>()
        };

        var plan = new ExecutionPlan();
        var options = new ImportOptions { ContinueOnError = true };
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

    private void SetupEmptyMetadataResponse()
    {
        var response = new RetrieveAllEntitiesResponse();
        response.Results["EntityMetadata"] = Array.Empty<EntityMetadata>();

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveAllEntitiesRequest)))
            .ReturnsAsync(response);
    }

    private void SetupMetadataResponse(string intersectEntityName, string schemaName)
    {
        var relationship = new ManyToManyRelationshipMetadata
        {
            SchemaName = schemaName
        };

        // Use reflection to set IntersectEntityName (it has a private setter)
        var intersectProp = typeof(ManyToManyRelationshipMetadata)
            .GetProperty(nameof(ManyToManyRelationshipMetadata.IntersectEntityName));
        intersectProp!.SetValue(relationship, intersectEntityName);

        var entityMetadata = new EntityMetadata();
        var m2mProp = typeof(EntityMetadata)
            .GetProperty(nameof(EntityMetadata.ManyToManyRelationships));
        m2mProp!.SetValue(entityMetadata, new[] { relationship });

        var response = new RetrieveAllEntitiesResponse();
        response.Results["EntityMetadata"] = new[] { entityMetadata };

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveAllEntitiesRequest)))
            .ReturnsAsync(response);
    }

    #endregion
}
