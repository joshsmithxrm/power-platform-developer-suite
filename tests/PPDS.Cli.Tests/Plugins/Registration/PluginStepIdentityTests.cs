using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public class PluginStepIdentityTests
{
    private static PluginStepConfig Config(
        string message = "Update",
        string entity = "account",
        string? secondaryEntity = null,
        string stage = "PostOperation",
        string mode = "Synchronous",
        string? name = null,
        int executionOrder = 1,
        string? filteringAttributes = null) => new()
    {
        Name = name,
        Message = message,
        Entity = entity,
        SecondaryEntity = secondaryEntity,
        Stage = stage,
        Mode = mode,
        ExecutionOrder = executionOrder,
        FilteringAttributes = filteringAttributes
    };

    private static PluginStepInfo Env(
        string message = "Update",
        string primaryEntity = "account",
        string? secondaryEntity = null,
        string stage = "PostOperation",
        string mode = "Synchronous",
        string name = "env-name",
        int executionOrder = 1,
        string? filteringAttributes = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Message = message,
        PrimaryEntity = primaryEntity,
        SecondaryEntity = secondaryEntity,
        Stage = stage,
        Mode = mode,
        ExecutionOrder = executionOrder,
        FilteringAttributes = filteringAttributes
    };

    [Fact]
    public void FromConfig_And_FromEnvironment_Agree_AcrossCasingAndWhitespace()
    {
        var config = Config(message: "Update", entity: "account", stage: "PostOperation", mode: "Synchronous");
        // Same behavior, but every component differs in casing/whitespace, plus a different name/rank/filter.
        var env = Env(
            message: "  update ",
            primaryEntity: "ACCOUNT",
            stage: "postoperation",
            mode: "SYNCHRONOUS",
            name: "a totally different display name",
            executionOrder: 42,
            filteringAttributes: "irrelevant");

        var idConfig = PluginStepIdentity.FromConfig("MyPlugin.Handler", config);
        var idEnv = PluginStepIdentity.FromEnvironment("  myplugin.HANDLER  ", env);

        Assert.Equal(idConfig, idEnv);
        Assert.Equal(idConfig.GetHashCode(), idEnv.GetHashCode());
    }

    [Fact]
    public void PrimaryEntity_EmptyNoneAndWhitespace_NormalizeEqual()
    {
        var fromEmpty = PluginStepIdentity.FromConfig("T", Config(entity: ""));
        var fromNone = PluginStepIdentity.FromEnvironment("T", Env(primaryEntity: "none"));
        var fromWhitespace = PluginStepIdentity.FromEnvironment("T", Env(primaryEntity: "  "));

        Assert.Equal(fromEmpty, fromNone);
        Assert.Equal(fromEmpty, fromWhitespace);
        Assert.Equal(PluginStepIdentity.NoEntity, fromEmpty.PrimaryEntity);
    }

    [Fact]
    public void SecondaryEntity_NullEmptyAndNone_NormalizeEqual()
    {
        var fromNull = PluginStepIdentity.FromConfig("T", Config(secondaryEntity: null));
        var fromEmpty = PluginStepIdentity.FromConfig("T", Config(secondaryEntity: ""));
        var fromNone = PluginStepIdentity.FromEnvironment("T", Env(secondaryEntity: "none"));

        Assert.Equal(fromNull, fromEmpty);
        Assert.Equal(fromNull, fromNone);
        Assert.Equal(PluginStepIdentity.NoEntity, fromNull.SecondaryEntity);
    }

    [Fact]
    public void Stage_Change_ChangesIdentity()
    {
        var post = PluginStepIdentity.FromConfig("T", Config(stage: "PostOperation"));
        var pre = PluginStepIdentity.FromConfig("T", Config(stage: "PreOperation"));

        Assert.NotEqual(post, pre);
    }

    [Fact]
    public void Mode_Change_ChangesIdentity()
    {
        var sync = PluginStepIdentity.FromConfig("T", Config(mode: "Synchronous"));
        var async = PluginStepIdentity.FromConfig("T", Config(mode: "Asynchronous"));

        Assert.NotEqual(sync, async);
    }

    [Fact]
    public void PluginTypeName_Change_ChangesIdentity()
    {
        var a = PluginStepIdentity.FromConfig("Plugin.TypeA", Config());
        var b = PluginStepIdentity.FromConfig("Plugin.TypeB", Config());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Message_Or_Entity_Change_ChangesIdentity()
    {
        var baseline = PluginStepIdentity.FromConfig("T", Config(message: "Update", entity: "account"));

        Assert.NotEqual(baseline, PluginStepIdentity.FromConfig("T", Config(message: "Create", entity: "account")));
        Assert.NotEqual(baseline, PluginStepIdentity.FromConfig("T", Config(message: "Update", entity: "contact")));
    }

    [Fact]
    public void Rank_Filtering_And_Name_DoNotAffectIdentity()
    {
        var a = PluginStepIdentity.FromConfig("T", Config(name: "Alpha", executionOrder: 1, filteringAttributes: "name"));
        var b = PluginStepIdentity.FromConfig("T", Config(name: "Omega", executionOrder: 999, filteringAttributes: "name,age,city"));

        Assert.Equal(a, b);
    }

    // Fix (#1295 follow-up): the environment side is always canonical, but a hand-authored config may
    // use variant tokens the deploy write path accepts. These must canonicalize to the SAME identity so
    // deploy updates in place instead of force-creating a duplicate every run.
    [Theory]
    [InlineData("40", "sync")]                 // numeric stage + short mode token
    [InlineData("PostOperation", "Synchronous")] // canonical (sanity: canonicalization is a no-op here)
    [InlineData("", "")]                        // omitted stage/mode default to PostOperation/Synchronous
    public void VariantStageAndModeTokens_CanonicalizeToEnvironmentIdentity(string stageToken, string modeToken)
    {
        var env = PluginStepIdentity.FromEnvironment("T", Env(stage: "PostOperation", mode: "Synchronous"));
        var config = PluginStepIdentity.FromConfig("T", Config(stage: stageToken, mode: modeToken));

        Assert.Equal(env, config);
    }

    [Fact]
    public void NumericStageToken_ForDifferentStage_StillDiffers()
    {
        // Guard against over-canonicalization collapsing everything to PostOperation: "20" is PreOperation.
        var post = PluginStepIdentity.FromEnvironment("T", Env(stage: "PostOperation"));
        var pre = PluginStepIdentity.FromConfig("T", Config(stage: "20"));

        Assert.NotEqual(post, pre);
    }

    // Guard boundary for #1332: "specified" means the value does not normalize to the NoEntity
    // sentinel — only then is a null SDK message filter a configuration error.
    [Theory]
    [InlineData("account", true)]
    [InlineData(" account ", true)]
    [InlineData("acount", true)] // a typo is still "specified" — that is exactly the error case
    [InlineData("none", false)]
    [InlineData("NONE", false)]
    [InlineData(" none ", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsEntitySpecified_DistinguishesRealEntitiesFromNoneSentinel(string? entity, bool expected)
    {
        Assert.Equal(expected, PluginStepIdentity.IsEntitySpecified(entity));
    }

    [Fact]
    public void ToString_RendersReadableComponents()
    {
        var id = PluginStepIdentity.FromConfig(
            "MyPlugin.Handler",
            Config(message: "Update", entity: "account", stage: "PostOperation", mode: "Synchronous"));

        var text = id.ToString();

        Assert.Contains("myplugin.handler", text);
        Assert.Contains("update", text);
        Assert.Contains("account", text);
        Assert.Contains("postoperation", text);
        Assert.Contains("synchronous", text);
    }
}
