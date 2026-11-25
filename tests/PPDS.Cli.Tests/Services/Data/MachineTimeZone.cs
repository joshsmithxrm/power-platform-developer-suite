using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// Runs a body under several machine time zones. The time zone is process-wide, so a class that uses this joins
/// <see cref="Collection"/>, which xUnit runs alone, after the parallel collections.
/// </summary>
internal static class MachineTimeZone
{
    public const string Collection = "Machine time zone";

    /// <summary>UTC, a zone behind it and the zone furthest ahead (UTC+14), where "today" is a different date.</summary>
    public static readonly string[] Zones = ["UTC", "America/Los_Angeles", "Pacific/Kiritimati"];

    /// <summary>
    /// Runs <paramref name="body"/> once under each of <see cref="Zones"/> (on Linux and macOS, through <c>TZ</c>; on
    /// Windows the zone cannot be changed in-process, and the body runs under the machine's zone each time).
    /// </summary>
    public static async Task<List<T>> InEachAsync<T>(Func<Task<T>> body)
    {
        var results = new List<T>();
        var saved = System.Environment.GetEnvironmentVariable("TZ");
        try
        {
            foreach (var zone in Zones)
            {
                if (!OperatingSystem.IsWindows())
                {
                    System.Environment.SetEnvironmentVariable("TZ", zone);
                    TimeZoneInfo.ClearCachedData();
                    Assert.Equal(zone, TimeZoneInfo.Local.Id);
                }

                results.Add(await body());
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TZ", saved);
            TimeZoneInfo.ClearCachedData();
        }

        return results;
    }
}

[CollectionDefinition(MachineTimeZone.Collection, DisableParallelization = true)]
public sealed class MachineTimeZoneCollection;
