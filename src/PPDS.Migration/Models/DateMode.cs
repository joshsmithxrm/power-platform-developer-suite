namespace PPDS.Migration.Models
{
    /// <summary>
    /// Determines how date/time fields are adjusted during import.
    /// </summary>
    public enum DateMode
    {
        /// <summary>Import the original date/time value unchanged.</summary>
        Absolute,

        /// <summary>Shift by whole weeks (round elapsed to nearest 7-day period).</summary>
        Relative,

        /// <summary>Shift by whole days (round elapsed to nearest day).</summary>
        RelativeDaily,

        /// <summary>Shift by the exact elapsed time.</summary>
        RelativeExact
    }
}
