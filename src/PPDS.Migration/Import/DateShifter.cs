using System;
using PPDS.Migration.Models;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Shifts date/time values based on the configured <see cref="DateMode"/>.
    /// </summary>
    public static class DateShifter
    {
        /// <summary>
        /// Shifts a date/time value based on the specified mode and elapsed time.
        /// </summary>
        /// <param name="value">The original date/time value, or null.</param>
        /// <param name="mode">The date shifting mode.</param>
        /// <param name="elapsed">The elapsed time since the original export.</param>
        /// <returns>The shifted date/time value, or null if <paramref name="value"/> is null.</returns>
        public static DateTime? Shift(DateTime? value, DateMode mode, TimeSpan elapsed)
        {
            if (value is null)
                return null;

            return mode switch
            {
                DateMode.Absolute => value,
                DateMode.Relative => value.Value.Add(RoundToWeeks(elapsed)),
                DateMode.RelativeDaily => value.Value.Add(RoundToDays(elapsed)),
                DateMode.RelativeExact => value.Value.Add(elapsed),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DateMode.")
            };
        }

        private static TimeSpan RoundToWeeks(TimeSpan elapsed)
        {
            var weeks = (long)Math.Round(elapsed.TotalDays / 7.0);
            return TimeSpan.FromDays((double)weeks * 7);
        }

        private static TimeSpan RoundToDays(TimeSpan elapsed)
        {
            var days = (long)Math.Round(elapsed.TotalDays);
            return TimeSpan.FromDays(days);
        }
    }
}
