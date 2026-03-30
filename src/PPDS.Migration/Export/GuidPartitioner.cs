using System;

namespace PPDS.Migration.Export
{
    /// <summary>
    /// Represents a range of GUIDs for partitioned export queries.
    /// </summary>
    public readonly struct GuidRange
    {
        /// <summary>
        /// Inclusive lower bound. Null means no lower bound (first partition).
        /// </summary>
        public Guid? LowerBound { get; }

        /// <summary>
        /// Exclusive upper bound. Null means no upper bound (last partition).
        /// </summary>
        public Guid? UpperBound { get; }

        /// <summary>
        /// True when the range covers the entire GUID space.
        /// </summary>
        public bool IsFull => !LowerBound.HasValue && !UpperBound.HasValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="GuidRange"/> struct.
        /// </summary>
        /// <param name="lowerBound">Inclusive lower bound, or null for no lower bound.</param>
        /// <param name="upperBound">Exclusive upper bound, or null for no upper bound.</param>
        public GuidRange(Guid? lowerBound, Guid? upperBound)
        {
            LowerBound = lowerBound;
            UpperBound = upperBound;
        }

        /// <summary>
        /// A range covering the entire GUID space.
        /// </summary>
        public static GuidRange Full => new(null, null);
    }

    /// <summary>
    /// Splits the GUID space into non-overlapping ranges for parallel export.
    /// </summary>
    /// <remarks>
    /// Partitions on byte index 10 of the .NET Guid byte array, which corresponds
    /// to the most-significant comparison byte in SQL Server's uniqueidentifier ordering.
    /// SQL Server compares GUIDs starting from the last 6 bytes (bytes 10-15), making
    /// byte 10 the highest-order comparison byte.
    /// </remarks>
    public static class GuidPartitioner
    {
        private const int MaxPartitions = 256;
        private const int SqlServerMostSignificantByteIndex = 10;

        /// <summary>
        /// Creates non-overlapping GUID range partitions that cover the entire GUID space.
        /// </summary>
        /// <param name="partitionCount">Number of partitions to create.</param>
        /// <returns>Array of GUID ranges where each range's upper bound equals the next range's lower bound.</returns>
        public static GuidRange[] CreatePartitions(int partitionCount)
        {
            if (partitionCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(partitionCount), "Partition count must be at least 1.");

            if (partitionCount == 1)
                return new[] { GuidRange.Full };

            if (partitionCount > MaxPartitions)
                partitionCount = MaxPartitions;

            var ranges = new GuidRange[partitionCount];
            var step = 256.0 / partitionCount;

            for (int i = 0; i < partitionCount; i++)
            {
                Guid? lower = i == 0 ? null : CreateBoundaryGuid((byte)Math.Round(i * step));
                Guid? upper = i == partitionCount - 1 ? null : CreateBoundaryGuid((byte)Math.Round((i + 1) * step));
                ranges[i] = new GuidRange(lower, upper);
            }

            return ranges;
        }

        /// <summary>
        /// Creates a boundary GUID where byte 10 (SQL Server's most-significant
        /// comparison byte) is set to the specified value and all other bytes are zero.
        /// </summary>
        internal static Guid CreateBoundaryGuid(byte msByte)
        {
            var bytes = new byte[16];
            bytes[SqlServerMostSignificantByteIndex] = msByte;
            return new Guid(bytes);
        }
    }
}
