using System;
using System.Globalization;
using System.Text.RegularExpressions;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.PluginTraces;

/// <summary>
/// The age a plug-in trace clean-up deletes by, and the cut-off it is turned into.
/// </summary>
/// <remarks>
/// An age is written in one exact form: a whole number from 1, with no sign, no leading zero and
/// nothing around it, followed directly by <c>d</c> (days), <c>h</c> (hours) or <c>m</c> (minutes),
/// for example <c>7d</c>, <c>24h</c> or <c>30m</c>. Anything else is refused, among it 0, a negative
/// number, a number with no unit, an upper-case unit and trailing text. An age is at most
/// <see cref="MaxAge"/>. The cut-off is the current UTC time less the age, computed once by the
/// caller and then used for the count and the delete alike.
/// </remarks>
public static class PluginTraceAge
{
    /// <summary>How the accepted form is described in errors and help.</summary>
    public const string FormDescription =
        "a whole number from 1 followed by d (days), h (hours) or m (minutes), for example 7d, 24h or 30m";

    /// <summary>The longest age accepted (100 years of 365 days).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(36500);

    /// <summary>The longest age accepted, in days.</summary>
    public const int MaxAgeDays = 36500;

    /// <summary>
    /// The shortest age the form can express (<c>1m</c>). A cut-off handed to the service directly must
    /// be at least this far in the past.
    /// </summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(1);

    // \A and \z: `$` would also match before a trailing newline.
    private static readonly Regex Form = new(
        @"\A(?<n>[1-9][0-9]{0,7})(?<unit>[dhm])\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Parses an age written in the one accepted form.
    /// </summary>
    /// <param name="text">The age as typed, for example <c>7d</c>.</param>
    /// <returns>The age, greater than zero and at most <see cref="MaxAge"/>.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> for anything not in the accepted form.
    /// </exception>
    public static TimeSpan Parse(string? text)
    {
        var match = text is null ? Match.Empty : Form.Match(text);
        if (!match.Success)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"The age to delete plug-in traces by must be {FormDescription}; nothing else is accepted.");
        }

        var number = long.Parse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var age = match.Groups["unit"].Value switch
        {
            "d" => TimeSpan.FromDays(Math.Min(number, 36501)),
            "h" => TimeSpan.FromHours(Math.Min(number, 36501L * 24)),
            _ => TimeSpan.FromMinutes(Math.Min(number, 36501L * 24 * 60)),
        };
        EnsureValid(age);
        return age;
    }

    /// <summary>
    /// An age given as a whole number of days (the daemon, the MCP tool and the TUI), checked before it
    /// is converted, so a value too large for a <see cref="TimeSpan"/> is refused like any other.
    /// </summary>
    /// <param name="days">The age in days, from 1 to <see cref="MaxAgeDays"/>.</param>
    /// <returns>The age.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> for 0, a negative number or more than <see cref="MaxAgeDays"/>.
    /// </exception>
    public static TimeSpan FromDays(int days)
    {
        if (days < 1 || days > MaxAgeDays)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"The age to delete plug-in traces by must be a whole number of days from 1 to {MaxAgeDays}.");
        }

        return TimeSpan.FromDays(days);
    }

    /// <summary>
    /// Refuses an age that is zero, negative or longer than <see cref="MaxAge"/>.
    /// </summary>
    /// <param name="age">The age to check.</param>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> when the age is not greater than zero or is too long.
    /// </exception>
    public static void EnsureValid(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                "The age to delete plug-in traces by must be greater than zero. " +
                "To delete every trace, use the separate option that deletes all traces.");
        }

        if (age > MaxAge)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                "The age to delete plug-in traces by can be at most 36500 days.");
        }
    }

    /// <summary>
    /// The cut-off for an age: <paramref name="utcNow"/> less <paramref name="age"/>, floored to the
    /// whole second, so the instant shown (<see cref="Format"/>) is the instant counted and deleted.
    /// Traces created strictly before it are older than the age (flooring can only delete fewer).
    /// </summary>
    /// <param name="age">The age, checked with <see cref="EnsureValid"/>.</param>
    /// <param name="utcNow">The current time, in UTC.</param>
    /// <returns>The cut-off, in UTC.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> for an age that is not valid.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="utcNow"/> is not a UTC time.</exception>
    public static DateTime CutoffUtc(TimeSpan age, DateTime utcNow)
    {
        EnsureValid(age);
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The current time must be given in UTC.", nameof(utcNow));
        }

        var cutoff = utcNow - age;
        return new DateTime(cutoff.Ticks - (cutoff.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>The cut-off as shown to the user: ISO 8601 in UTC, to the second.</summary>
    /// <param name="cutoffUtc">The cut-off, in UTC.</param>
    /// <returns>For example <c>2026-11-18T09:30:00Z</c>.</returns>
    public static string Format(DateTime cutoffUtc)
        => cutoffUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
