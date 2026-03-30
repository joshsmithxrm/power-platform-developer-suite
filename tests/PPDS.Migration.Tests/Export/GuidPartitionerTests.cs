using System;
using FluentAssertions;
using PPDS.Migration.Export;
using Xunit;

namespace PPDS.Migration.Tests.Export;

public class GuidPartitionerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void CreatePartitions_ReturnsRequestedCount(int count)
    {
        var partitions = GuidPartitioner.CreatePartitions(count);

        partitions.Should().HaveCount(count);
    }

    [Fact]
    public void SinglePartition_ReturnsFull()
    {
        var partitions = GuidPartitioner.CreatePartitions(1);

        partitions.Should().HaveCount(1);
        partitions[0].IsFull.Should().BeTrue();
        partitions[0].LowerBound.Should().BeNull();
        partitions[0].UpperBound.Should().BeNull();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(256)]
    public void PartitionsCoverFullGuidSpace(int count)
    {
        var partitions = GuidPartitioner.CreatePartitions(count);

        // First partition has no lower bound
        partitions[0].LowerBound.Should().BeNull();

        // Last partition has no upper bound
        partitions[^1].UpperBound.Should().BeNull();

        // Each partition's upper bound equals the next partition's lower bound (no gaps)
        for (int i = 0; i < partitions.Length - 1; i++)
        {
            partitions[i].UpperBound.Should().NotBeNull();
            partitions[i + 1].LowerBound.Should().NotBeNull();
            partitions[i].UpperBound.Should().Be(partitions[i + 1].LowerBound,
                $"partition {i} upper bound should equal partition {i + 1} lower bound");
        }
    }

    [Fact]
    public void PartitionCount_CappedAt256()
    {
        var partitions = GuidPartitioner.CreatePartitions(1000);

        partitions.Should().HaveCount(256);
    }

    [Fact]
    public void ZeroPartitions_Throws()
    {
        var act = () => GuidPartitioner.CreatePartitions(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NegativePartitions_Throws()
    {
        var act = () => GuidPartitioner.CreatePartitions(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BoundaryGuids_HaveCorrectByteLayout()
    {
        // Two partitions split at byte10 = 128 (0x80)
        var partitions = GuidPartitioner.CreatePartitions(2);
        var boundary = partitions[0].UpperBound!.Value;
        var bytes = boundary.ToByteArray();

        // Byte index 10 is SQL Server's most-significant comparison byte
        bytes[10].Should().Be(0x80);

        // All other bytes should be zero
        for (int i = 0; i < 16; i++)
        {
            if (i != 10)
                bytes[i].Should().Be(0, $"byte {i} should be zero");
        }
    }

    [Fact]
    public void TwoPartitions_SplitAtMidpoint()
    {
        var partitions = GuidPartitioner.CreatePartitions(2);

        partitions[0].LowerBound.Should().BeNull();
        partitions[1].UpperBound.Should().BeNull();

        // The boundary should be at byte10 = 128 (0x80)
        var boundary = partitions[0].UpperBound!.Value;
        var bytes = boundary.ToByteArray();
        bytes[10].Should().Be(128);
    }

    [Fact]
    public void FourPartitions_HaveDistinctBoundaries()
    {
        var partitions = GuidPartitioner.CreatePartitions(4);

        // Collect all boundaries
        var boundaries = new Guid?[5];
        boundaries[0] = partitions[0].LowerBound; // null
        for (int i = 0; i < 4; i++)
            boundaries[i + 1] = partitions[i].UpperBound;

        // First is null, last is null, middle three are distinct
        boundaries[0].Should().BeNull();
        boundaries[4].Should().BeNull();
        boundaries[1].Should().NotBe(boundaries[2]!.Value);
        boundaries[2].Should().NotBe(boundaries[3]!.Value);
    }
}
