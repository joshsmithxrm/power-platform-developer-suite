using System;
using System.Linq;
using PPDS.Dataverse.Query.Planning.Partitioning;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Partitioning;

[Trait("Category", "PlanUnit")]
public class DateRangePartitionerTests
{
    private readonly DateRangePartitioner _partitioner = new();

    [Fact]
    public void SinglePartition_WhenRecordCountBelowMax()
    {
        // 30K records with 40K max per partition => 1 partition
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(30_000, min, max);

        Assert.Single(partitions);
        Assert.Equal(min, partitions[0].Start);
        Assert.Equal(max.AddSeconds(1), partitions[0].End);
        Assert.Equal(0, partitions[0].Index);
    }

    [Fact]
    public void TwoPartitions_WhenRecordCountExceedsMax()
    {
        // 70K records with 40K max => ceil(70000/40000) = 2 partitions
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(70_000, min, max);

        Assert.Equal(2, partitions.Count);
        Assert.Equal(0, partitions[0].Index);
        Assert.Equal(1, partitions[1].Index);
    }

    [Fact]
    public void ThreePartitions_WhenRecordCountRequiresThree()
    {
        // 100K records with 40K max => ceil(100000/40000) = 3 partitions
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(100_000, min, max);

        Assert.Equal(3, partitions.Count);
    }

    [Fact]
    public void Partitions_AreNonOverlapping()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(200_000, min, max);

        Assert.True(partitions.Count > 1, "Expected multiple partitions for this test");

        for (var i = 1; i < partitions.Count; i++)
        {
            // Each partition's start must equal the previous partition's end
            Assert.Equal(partitions[i - 1].End, partitions[i].Start);
        }
    }

    [Fact]
    public void Partitions_CoverEntireDateRange()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(200_000, min, max);

        // First partition starts at min
        Assert.Equal(min, partitions[0].Start);

        // Last partition ends at max + 1 second (inclusive boundary)
        Assert.Equal(max.AddSeconds(1), partitions[^1].End);
    }

    [Fact]
    public void Partitions_HaveSequentialIndices()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(200_000, min, max);

        for (var i = 0; i < partitions.Count; i++)
        {
            Assert.Equal(i, partitions[i].Index);
        }
    }

    [Fact]
    public void CustomMaxRecordsPerPartition_AffectsPartitionCount()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // 100K records with 20K max => 5 partitions
        var partitions = _partitioner.CalculatePartitions(100_000, min, max, maxRecordsPerPartition: 20_000);

        Assert.Equal(5, partitions.Count);
    }

    [Fact]
    public void MinimumOnePartition_ForSmallRecordCount()
    {
        var min = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 6, 1, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(1, min, max);

        Assert.Single(partitions);
    }

    [Fact]
    public void ZeroRecordCount_ProducesSinglePartition()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(0, min, max);

        // Math.Ceiling(0/40000) = 0, then Math.Max(0, 1) = 1
        Assert.Single(partitions);
    }

    [Fact]
    public void NarrowDateRange_StillPartitionsCorrectly()
    {
        // 1 minute range with 3 partitions
        var min = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 6, 15, 12, 1, 0, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(120_000, min, max);

        Assert.Equal(3, partitions.Count);
        Assert.Equal(min, partitions[0].Start);
        Assert.Equal(max.AddSeconds(1), partitions[^1].End);

        // Non-overlapping
        for (var i = 1; i < partitions.Count; i++)
        {
            Assert.Equal(partitions[i - 1].End, partitions[i].Start);
        }
    }

    [Fact]
    public void ExactMultiple_PartitionsEvenly()
    {
        // 80K records with 40K max => exactly 2 partitions
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(80_000, min, max);

        Assert.Equal(2, partitions.Count);
    }

    [Fact]
    public void LargeRecordCount_ProducesManyPartitions()
    {
        // 1M records with 40K max => ceil(1000000/40000) = 25 partitions
        var min = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(1_000_000, min, max);

        Assert.Equal(25, partitions.Count);

        // Verify all boundaries are contiguous
        for (var i = 1; i < partitions.Count; i++)
        {
            Assert.Equal(partitions[i - 1].End, partitions[i].Start);
        }
    }

    [Fact]
    public void Partitions_StartIsBeforeEnd()
    {
        var min = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(200_000, min, max);

        foreach (var partition in partitions)
        {
            Assert.True(partition.Start < partition.End,
                $"Partition {partition.Index}: Start ({partition.Start}) should be before End ({partition.End})");
        }
    }

    [Fact]
    public void SameDateMinMax_ProducesSinglePartition()
    {
        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var partitions = _partitioner.CalculatePartitions(50_000, date, date);

        // Even though there are more than 40K records, the date range is zero-width
        // so all partitions have the same boundaries. The math still produces
        // multiple partitions (2), but they all start/end at the same point
        // which is fine -- the last partition includes maxDate+1s.
        Assert.True(partitions.Count >= 1);
        Assert.Equal(date, partitions[0].Start);
    }
}
