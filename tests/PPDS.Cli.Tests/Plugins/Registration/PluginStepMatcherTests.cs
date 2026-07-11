using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public class PluginStepMatcherTests
{
    private static readonly PluginTypeConfig TypeConfig = new() { TypeName = "MyPlugin.Handler" };
    private static readonly PluginTypeInfo TypeInfo = new() { Id = Guid.NewGuid(), TypeName = "MyPlugin.Handler" };

    private static PluginStepConfig Config(
        string stage = "PostOperation",
        string mode = "Synchronous",
        string message = "Update",
        string entity = "account",
        string? name = null,
        int executionOrder = 1,
        string? filteringAttributes = null) => new()
    {
        Name = name,
        Message = message,
        Entity = entity,
        Stage = stage,
        Mode = mode,
        ExecutionOrder = executionOrder,
        FilteringAttributes = filteringAttributes
    };

    private static PluginStepInfo Env(
        string stage = "PostOperation",
        string mode = "Synchronous",
        string message = "Update",
        string primaryEntity = "account",
        string name = "MyPlugin.Handler: Update of account",
        int executionOrder = 1,
        Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Message = message,
        PrimaryEntity = primaryEntity,
        Stage = stage,
        Mode = mode,
        ExecutionOrder = executionOrder
    };

    private static (PluginTypeConfig, PluginStepConfig) Cfg(PluginStepConfig step) => (TypeConfig, step);
    private static (PluginTypeInfo, PluginStepInfo) Ev(PluginStepInfo step) => (TypeInfo, step);

    // The reported issue (#1295): the environment holds two steps that share a display name but
    // differ in stage, and the config declares a single step matching one of them. This must NOT
    // throw; it must pair the matching stage and orphan the other.
    [Fact]
    public void SameNamedDifferentStage_PairsCorrectStage_OrphansTheOther()
    {
        var sharedName = "MyPlugin.Handler: Update of account";
        var config = Config(stage: "PostOperation", name: sharedName);
        var envPost = Env(stage: "PostOperation", name: sharedName);
        var envPre = Env(stage: "PreOperation", name: sharedName);

        var matches = PluginStepMatcher.Match(
            new[] { Cfg(config) },
            new[] { Ev(envPost), Ev(envPre) });

        var paired = matches.Where(m => m.IsPaired).ToList();
        var orphaned = matches.Where(m => m.IsOrphaned).ToList();
        var missing = matches.Where(m => m.IsMissing).ToList();

        Assert.Single(paired);
        Assert.Single(orphaned);
        Assert.Empty(missing);

        // Paired with the PostOperation row (the one whose identity matches the config).
        Assert.Equal(envPost.Id, paired[0].Env!.Id);
        Assert.Same(config, paired[0].Config);

        // The PreOperation row is the orphan.
        Assert.Equal(envPre.Id, orphaned[0].Env!.Id);
    }

    [Fact]
    public void ResidualCollision_TwoIdenticalEnvSteps_FiresCallback_And_StillClassifies()
    {
        var config = Config(stage: "PostOperation");
        var envA = Env(stage: "PostOperation", id: Guid.NewGuid());
        var envB = Env(stage: "PostOperation", id: Guid.NewGuid()); // identical functional identity

        var collisions = new List<(PluginStepIdentity Identity, int ConfigCount, int EnvCount)>();

        var matches = PluginStepMatcher.Match(
            new[] { Cfg(config) },
            new[] { Ev(envA), Ev(envB) },
            onResidualCollision: (id, cc, ec) => collisions.Add((id, cc, ec)));

        var collision = Assert.Single(collisions);
        Assert.Equal(1, collision.ConfigCount);
        Assert.Equal(2, collision.EnvCount);

        // Still classifies: one pair, one orphan, no exception.
        Assert.Single(matches.Where(m => m.IsPaired));
        Assert.Single(matches.Where(m => m.IsOrphaned));
        Assert.Empty(matches.Where(m => m.IsMissing));
    }

    [Fact]
    public void ConfigSurplus_ProducesMissing()
    {
        // Two configured steps share an identity; only one environment step exists.
        var cfgA = Config(name: "A");
        var cfgB = Config(name: "B");
        var env = Env();

        var collisions = new List<(PluginStepIdentity Identity, int ConfigCount, int EnvCount)>();
        var matches = PluginStepMatcher.Match(
            new[] { Cfg(cfgA), Cfg(cfgB) },
            new[] { Ev(env) },
            onResidualCollision: (id, cc, ec) => collisions.Add((id, cc, ec)));

        Assert.Single(matches.Where(m => m.IsPaired));
        Assert.Single(matches.Where(m => m.IsMissing));
        Assert.Empty(matches.Where(m => m.IsOrphaned));

        var collision = Assert.Single(collisions);
        Assert.Equal(2, collision.ConfigCount);
        Assert.Equal(1, collision.EnvCount);
    }

    [Fact]
    public void EnvSurplus_ProducesOrphaned()
    {
        var config = Config();
        var env1 = Env(id: Guid.NewGuid());
        var env2 = Env(id: Guid.NewGuid());

        var matches = PluginStepMatcher.Match(
            new[] { Cfg(config) },
            new[] { Ev(env1), Ev(env2) });

        Assert.Single(matches.Where(m => m.IsPaired));
        Assert.Single(matches.Where(m => m.IsOrphaned));
        Assert.Empty(matches.Where(m => m.IsMissing));
    }

    [Fact]
    public void DisjointIdentities_ClassifyAsMissingAndOrphaned()
    {
        var config = Config(stage: "PostOperation", message: "Update", entity: "account");
        var env = Env(stage: "PreOperation", message: "Create", primaryEntity: "contact");

        var matches = PluginStepMatcher.Match(new[] { Cfg(config) }, new[] { Ev(env) });

        Assert.Single(matches.Where(m => m.IsMissing));
        Assert.Single(matches.Where(m => m.IsOrphaned));
        Assert.Empty(matches.Where(m => m.IsPaired));
        // No residual-collision expectation: each identity group has a single member.
    }

    [Fact]
    public void WithinIdentityGroup_PairingIsDeterministic_ByExecutionOrder()
    {
        // Four members share one identity (rank is not part of identity). Config and env are supplied
        // out of order; the matcher must sort both sides so rank-1 pairs with rank-1 and rank-5 with rank-5.
        var cfgRank1 = Config(name: "zzz", executionOrder: 1);
        var cfgRank5 = Config(name: "aaa", executionOrder: 5);
        var envRank1 = Env(executionOrder: 1, id: Guid.NewGuid());
        var envRank5 = Env(executionOrder: 5, id: Guid.NewGuid());

        var matches = PluginStepMatcher.Match(
            new[] { Cfg(cfgRank5), Cfg(cfgRank1) },
            new[] { Ev(envRank5), Ev(envRank1) });

        var paired = matches.Where(m => m.IsPaired).ToList();
        Assert.Equal(2, paired.Count);

        // Deterministic: each pair matches like-ranked config and env.
        foreach (var pair in paired)
            Assert.Equal(pair.Config!.ExecutionOrder, pair.Env!.ExecutionOrder);
    }

    [Fact]
    public void EmptyInputs_ReturnEmpty()
    {
        var matches = PluginStepMatcher.Match(
            Array.Empty<(PluginTypeConfig, PluginStepConfig)>(),
            Array.Empty<(PluginTypeInfo, PluginStepInfo)>());

        Assert.Empty(matches);
    }
}
