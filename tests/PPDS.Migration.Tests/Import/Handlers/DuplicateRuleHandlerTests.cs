using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class DuplicateRuleHandlerTests
{
    private readonly Mock<IDataverseConnectionPool> _connectionPool;
    private readonly DuplicateRuleHandler _handler;
    private readonly ImportContext _context;

    public DuplicateRuleHandlerTests()
    {
        _connectionPool = new Mock<IDataverseConnectionPool>();
        _handler = new DuplicateRuleHandler(_connectionPool.Object);
        _context = new ImportContext(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
    }

    [Fact]
    public void CanHandle_DuplicateRule_ReturnsTrue()
    {
        _handler.CanHandle("duplicaterule").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_OtherEntity_ReturnsFalse()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void RemapsEntityTypeCodes()
    {
        // Source environment: ETC 1 = "account"
        // Target environment: "account" = ETC 2
        _context.SourceEntityTypeCodes = new Dictionary<int, string>
        {
            [1] = "account",
            [10] = "contact"
        };
        _context.TargetEntityTypeCodes = new Dictionary<string, int>
        {
            ["account"] = 2,
            ["contact"] = 20
        };

        var record = new Entity("duplicaterule") { Id = Guid.NewGuid() };
        record["baseentitytypecode"] = 1;
        record["matchingentitytypecode"] = 10;
        record["name"] = "Test Rule";

        var result = _handler.Transform(record, _context);

        result.GetAttributeValue<int>("baseentitytypecode").Should().Be(2);
        result.GetAttributeValue<int>("matchingentitytypecode").Should().Be(20);
        result.GetAttributeValue<string>("name").Should().Be("Test Rule");
    }

    [Fact]
    public void Transform_NoTargetEtcMapping_LeavesFieldUnchanged()
    {
        // No ETC mappings configured
        var record = new Entity("duplicaterule") { Id = Guid.NewGuid() };
        record["baseentitytypecode"] = 1;

        var result = _handler.Transform(record, _context);

        result.GetAttributeValue<int>("baseentitytypecode").Should().Be(1);
    }

    [Fact]
    public async Task PublishesRulesPostImport()
    {
        // Set up ID mappings for two imported duplicate rules
        var sourceId1 = Guid.NewGuid();
        var targetId1 = Guid.NewGuid();
        var sourceId2 = Guid.NewGuid();
        var targetId2 = Guid.NewGuid();

        _context.IdMappings.AddMapping("duplicaterule", sourceId1, targetId1);
        _context.IdMappings.AddMapping("duplicaterule", sourceId2, targetId2);

        // Set up mock pooled client
        var mockClient = new Mock<IPooledClient>();
        mockClient.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());
        mockClient.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _connectionPool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        // Act
        await _handler.ExecuteAsync("duplicaterule", _context, CancellationToken.None);

        // Verify PublishDuplicateRule was called for each rule
        mockClient.Verify(
            c => c.ExecuteAsync(It.Is<OrganizationRequest>(r =>
                r.RequestName == "PublishDuplicateRule" &&
                (Guid)r["DuplicateRuleId"] == targetId1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        mockClient.Verify(
            c => c.ExecuteAsync(It.Is<OrganizationRequest>(r =>
                r.RequestName == "PublishDuplicateRule" &&
                (Guid)r["DuplicateRuleId"] == targetId2),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoMappings_DoesNotCallPool()
    {
        // No mappings added — should not attempt to get a client
        await _handler.ExecuteAsync("duplicaterule", _context, CancellationToken.None);

        _connectionPool.Verify(
            p => p.GetClientAsync(It.IsAny<Dataverse.Client.DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
