using System;
using FluentAssertions;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "Unit")]
public class FetchXmlScanNodePagingTests
{
    private const string LinkedFetchXml = @"<fetch count='50'>
        <entity name='account'>
            <attribute name='name' />
            <link-entity name='contact' from='parentcustomerid' to='accountid'>
                <attribute name='fullname' />
            </link-entity>
        </entity>
    </fetch>";

    private const string SimpleFetchXml = @"<fetch count='50'>
        <entity name='account'>
            <attribute name='name' />
        </entity>
    </fetch>";

    private static FetchXmlScanNode CreateNode(string fetchXml, string entityLogicalName)
    {
        return new FetchXmlScanNode(fetchXml, entityLogicalName);
    }

    // ---------------------------------------------------------------
    // HasLinkedEntity detection
    // ---------------------------------------------------------------

    [Fact]
    public void HasLinkedEntity_DetectsLinkEntity()
    {
        var node = CreateNode(LinkedFetchXml, "account");
        node.HasLinkedEntity.Should().BeTrue();
    }

    [Fact]
    public void NoLinkedEntity_ReturnsFalse()
    {
        var node = CreateNode(SimpleFetchXml, "account");
        node.HasLinkedEntity.Should().BeFalse();
    }

    [Fact]
    public void HasLinkedEntity_DetectsNestedLinkEntity()
    {
        var fetchXml = @"<fetch count='50'>
            <entity name='account'>
                <attribute name='name' />
                <link-entity name='contact' from='parentcustomerid' to='accountid'>
                    <attribute name='fullname' />
                    <link-entity name='systemuser' from='systemuserid' to='ownerid'>
                        <attribute name='fullname' />
                    </link-entity>
                </link-entity>
            </entity>
        </fetch>";

        var node = CreateNode(fetchXml, "account");
        node.HasLinkedEntity.Should().BeTrue();
    }

    [Fact]
    public void HasLinkedEntity_MalformedXml_ReturnsFalse()
    {
        // Contains "link-entity" as text but is not valid XML
        var fetchXml = "<fetch count='50'><entity name='account'><link-entity";

        var node = CreateNode(fetchXml, "account");
        node.HasLinkedEntity.Should().BeFalse();
    }

    [Fact]
    public void HasLinkedEntity_NoLinkEntityText_SkipsXmlParsing()
    {
        // Ensures the fast-path string check works: no "link-entity" substring at all
        var fetchXml = @"<fetch count='50'>
            <entity name='account'>
                <attribute name='name' />
                <filter>
                    <condition attribute='statecode' operator='eq' value='0' />
                </filter>
            </entity>
        </fetch>";

        var node = CreateNode(fetchXml, "account");
        node.HasLinkedEntity.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // Page boundary tracking: ShouldMergeWithPreviousPage
    // ---------------------------------------------------------------

    [Fact]
    public void ShouldMergeLastParent_SameParentId_ReturnsTrue()
    {
        var node = CreateNode(LinkedFetchXml, "account");
        var parentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        node.SetLastParentId(parentId);

        node.ShouldMergeWithPreviousPage(parentId).Should().BeTrue();
    }

    [Fact]
    public void ShouldMergeLastParent_DifferentParentId_ReturnsFalse()
    {
        var node = CreateNode(LinkedFetchXml, "account");
        node.SetLastParentId(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        var shouldMerge = node.ShouldMergeWithPreviousPage(
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        shouldMerge.Should().BeFalse();
    }

    [Fact]
    public void ShouldMerge_NoLastParentIdSet_ReturnsFalse()
    {
        // Before any page has been fetched, _lastParentId is null
        var node = CreateNode(LinkedFetchXml, "account");

        node.ShouldMergeWithPreviousPage(
            Guid.Parse("00000000-0000-0000-0000-000000000001")).Should().BeFalse();
    }

    [Fact]
    public void ShouldMerge_NoLinkedEntity_ReturnsFalse_EvenWithMatchingIds()
    {
        // For simple queries without link-entity, merge should never activate
        var node = CreateNode(SimpleFetchXml, "account");
        var parentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        node.SetLastParentId(parentId);

        node.ShouldMergeWithPreviousPage(parentId).Should().BeFalse();
    }

    [Fact]
    public void SetLastParentId_OverwritesPreviousValue()
    {
        var node = CreateNode(LinkedFetchXml, "account");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        node.SetLastParentId(firstId);
        node.SetLastParentId(secondId);

        // Should match the new value, not the old one
        node.ShouldMergeWithPreviousPage(secondId).Should().BeTrue();
        node.ShouldMergeWithPreviousPage(firstId).Should().BeFalse();
    }

    [Fact]
    public void ShouldMerge_EmptyGuid_MatchesCorrectly()
    {
        var node = CreateNode(LinkedFetchXml, "account");
        node.SetLastParentId(Guid.Empty);

        node.ShouldMergeWithPreviousPage(Guid.Empty).Should().BeTrue();
        node.ShouldMergeWithPreviousPage(
            Guid.Parse("00000000-0000-0000-0000-000000000001")).Should().BeFalse();
    }
}
