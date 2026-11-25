using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The SQL service's protection for the connected environment is exactly what the shared resolver of #1891
/// (<see cref="WriteProtectionResolver.ResolveForSqlDml"/>) gives for the saved entry, whatever was configured before:
/// no entry, an entry with no type, Unknown, each defined type, a type or protection that is not a defined member, and
/// an explicit protection of each level. Every surface (CLI, TUI, daemon) reaches the service through
/// <see cref="SqlQueryService.ConfigureEnvironment"/>, so this pins all three.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlProtectionResolverParityTests
{
    private const string Url = "https://org.crm.dynamics.com";

    public static TheoryData<string, EnvironmentConfig?> Entries()
    {
        var data = new TheoryData<string, EnvironmentConfig?>
        {
            { "no entry", null },
            { "an entry with no type", new EnvironmentConfig { Url = Url, Label = "ORG" } },
            { "type 99 (not a defined member)", new EnvironmentConfig { Url = Url, Type = (EnvironmentType)99 } },
            { "protection 99 (not a defined member)", new EnvironmentConfig { Url = Url, Type = EnvironmentType.Development, Protection = (ProtectionLevel)99 } },
            { "protection 99 with no type", new EnvironmentConfig { Url = Url, Protection = (ProtectionLevel)99 } },
        };
        foreach (var type in Enum.GetValues<EnvironmentType>())
            data.Add($"type {type}", new EnvironmentConfig { Url = Url, Type = type });
        foreach (var protection in Enum.GetValues<ProtectionLevel>())
        {
            data.Add($"protection {protection} with no type", new EnvironmentConfig { Url = Url, Protection = protection });
            data.Add($"protection {protection} on Unknown", new EnvironmentConfig { Url = Url, Type = EnvironmentType.Unknown, Protection = protection });
            data.Add($"protection {protection} on Production", new EnvironmentConfig { Url = Url, Type = EnvironmentType.Production, Protection = protection });
            data.Add($"protection {protection} on Development", new EnvironmentConfig { Url = Url, Type = EnvironmentType.Development, Protection = protection });
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void TheServicesLevel_IsWhatTheSharedResolverGives_FromAnyEarlierLevel(string entry, EnvironmentConfig? config)
    {
        var expected = WriteProtectionResolver.ResolveForSqlDml(config);

        foreach (var earlier in Enum.GetValues<ProtectionLevel>())
        {
            var h = DmlTestHarness.Create(rows: 1, protection: earlier);
            h.Service.EnvironmentSafetySettings = new QuerySafetySettings { WarnInsertThreshold = 7 };

            h.Service.ConfigureEnvironment(Url, config);

            Assert.True(expected == h.Service.EnvironmentProtectionLevel,
                $"{entry}, configured after {earlier}: the service has {h.Service.EnvironmentProtectionLevel}, the resolver gives {expected}");
            Assert.Same(config?.SafetySettings, h.Service.EnvironmentSafetySettings);
        }
    }

    [Fact]
    public void AnEnvironmentWithNoSavedType_IsProduction()
    {
        // The case #1891 and this branch both decided: no entry, an entry without a type and Unknown are Production.
        foreach (var config in new EnvironmentConfig?[] { null, new() { Url = Url }, new() { Url = Url, Type = EnvironmentType.Unknown } })
        {
            var h = DmlTestHarness.Create(rows: 1, protection: ProtectionLevel.Development);
            h.Service.ConfigureEnvironment(Url, config);
            Assert.Equal(ProtectionLevel.Production, h.Service.EnvironmentProtectionLevel);
        }
    }
}
