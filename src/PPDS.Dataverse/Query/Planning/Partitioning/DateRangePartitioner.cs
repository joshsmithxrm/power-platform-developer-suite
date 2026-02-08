using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Planning.Partitioning;

/// <summary>
/// Generates non-overlapping date range partitions for splitting aggregate queries
/// that exceed the Dataverse AggregateQueryRecordLimit (50K records).
/// </summary>
public sealed class DateRangePartitioner
{
    /// <summary>
    /// Calculates partition boundaries for a given record count and date range.
    /// </summary>
    /// <param name="estimatedRecordCount">Estimated total records.</param>
    /// <param name="minDate">Earliest record date.</param>
    /// <param name="maxDate">Latest record date.</param>
    /// <param name="maxRecordsPerPartition">Maximum records per partition (default: 40000 to stay below 50K limit).</param>
    /// <returns>List of date range partitions.</returns>
    public IReadOnlyList<DateRangePartition> CalculatePartitions(
        long estimatedRecordCount,
        DateTime minDate,
        DateTime maxDate,
        int maxRecordsPerPartition = 40000)
    {
        // Calculate partition count: ceil(estimatedCount / maxRecordsPerPartition)
        var partitionCount = (int)Math.Ceiling((double)estimatedRecordCount / maxRecordsPerPartition);
        partitionCount = Math.Max(partitionCount, 1);

        var totalTicks = maxDate.Ticks - minDate.Ticks;
        var ticksPerPartition = totalTicks / partitionCount;

        var partitions = new List<DateRangePartition>();
        var currentStart = minDate;

        for (var i = 0; i < partitionCount; i++)
        {
            var isLast = i == partitionCount - 1;
            var currentEnd = isLast
                ? maxDate.AddSeconds(1) // Include the max date in the last partition
                : new DateTime(minDate.Ticks + ticksPerPartition * (i + 1), DateTimeKind.Utc);

            partitions.Add(new DateRangePartition(currentStart, currentEnd, i));
            currentStart = currentEnd;
        }

        return partitions;
    }
}

/// <summary>
/// A single date range partition with non-overlapping boundaries.
/// Start is inclusive, End is exclusive: [Start, End).
/// </summary>
public sealed class DateRangePartition
{
    /// <summary>Inclusive start of the date range.</summary>
    public DateTime Start { get; }

    /// <summary>Exclusive end of the date range.</summary>
    public DateTime End { get; }

    /// <summary>Zero-based index of this partition.</summary>
    public int Index { get; }

    public DateRangePartition(DateTime start, DateTime end, int index)
    {
        Start = start;
        End = end;
        Index = index;
    }
}
