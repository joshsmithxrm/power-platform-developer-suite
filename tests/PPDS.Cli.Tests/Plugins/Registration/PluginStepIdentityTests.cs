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
