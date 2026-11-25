using System.CommandLine;
using System.Reflection;
using System.ServiceModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;
using AuthoringUpdateRelationshipRequest = PPDS.Dataverse.Metadata.Authoring.UpdateRelationshipRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// #1919, review round 2: cascade text is parsed by one name-only parser on every surface; two updates on one service
/// each read the relationship; an undefined value is never written; the write's failures say whether anything changed;
/// the read is checked to be the relationship asked for; an update with nothing to change sends nothing; and the CLI
/// output is pinned.
/// </summary>
public sealed partial class RelationshipCascadeSettingMatrixTests
{
    // ---- The shared parser ----

    [Theory]
    [InlineData("Delete", "Restrict", CascadeBehavior.Restrict)]
    [InlineData("Delete", "restrict", CascadeBehavior.Restrict)]
    [InlineData("Delete", "  REMOVELINK \t", CascadeBehavior.RemoveLink)]
    [InlineData("Assign", "userOwned", CascadeBehavior.UserOwned)]
    [InlineData("Merge", " NoCascade", CascadeBehavior.NoCascade)]
    [InlineData("Unshare", "Active ", CascadeBehavior.Active)]
    public void TryParse_AcceptsTheNameOfAValueValidForTheAction(string action, string text, CascadeBehavior expected)
    {
        Assert.True(CascadeActionRules.TryParse(action, text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("Delete", "0")] // the enum's Cascade
    [InlineData("Delete", "5")] // the enum's Restrict
    [InlineData("Delete", "+5")]
    [InlineData("Delete", "05")]
    [InlineData("Delete", "-1")]
    [InlineData("Delete", "Active,RemoveLink")] // ORed by enum parsing, this is Restrict
    [InlineData("Delete", "Cascade, Restrict")]
    [InlineData("Delete", "Cascade Restrict")]
    [InlineData("Delete", "")]
    [InlineData("Delete", "   ")]
    [InlineData("Delete", null)]
    [InlineData("Delete", "NoCascade")] // a name, but not one Delete takes
    [InlineData("Assign", "Active,NoCascade")] // ORed, this is UserOwned
    [InlineData("Merge", "2")] // the enum's NoCascade
    [InlineData("Merge", "Restrict")]
    [InlineData("Share", "Cascade.")]
    public void TryParse_RefusesEverythingElse(string action, string? text)
    {
        Assert.False(CascadeActionRules.TryParse(action, text, out _));
    }

    [Fact]
    public void InvalidValueMessage_ListsOnlyThatActionsValues_AndNotTheTextGiven()
    {
        Assert.Equal("Invalid cascade delete behavior. Valid values: Cascade, RemoveLink or Restrict.", CascadeActionRules.InvalidValueMessage("Delete"));
        Assert.Equal("Invalid cascade merge behavior. Valid values: Cascade or NoCascade.", CascadeActionRules.InvalidValueMessage("Merge"));
    }

    // ---- MF1: the CLI's cascade options are text, refused before anything when they name no valid value ----

    public static TheoryData<string, string> CliTextThatNamesNoValidValue()
    {
        var data = new TheoryData<string, string>();
        foreach (var action in Six)
        {
            var valid = CascadeActionRules.ValidFor(action);
            var option = "--cascade-" + action.ToLowerInvariant();
            foreach (var text in new[] { "0", "1", "5", "+5", "05", "-1", $"{valid[0]},{valid[1]}", $"{valid[^1]},{valid[0]}", "", " " })
                data.Add(option, text);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CliTextThatNamesNoValidValue))]
    public async Task Cli_CascadeOption_TextThatNamesNoValidValue_IsRefusedBeforeAnything(string option, string text)
    {
        var env = new FakeEnvironment(Relationship("mixed"));
        var action = Six.Single(a => option == "--cascade-" + a.ToLowerInvariant());

        var run = await RunCliAsync(env, ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", option, text, "--output-format", "json"]);

        Assert.Equal(ExitCodes.ValidationError, run.Exit);
        Assert.Equal(0, run.ProviderCalls); // refused before connecting
        Assert.Empty(env.Reads);
        Assert.Empty(env.Sent);
        Assert.Contains("Valid values: " + CascadeActionRules.Describe(action) + ".", run.Stdout);
        Assert.Contains("--cascade-" + action.ToLowerInvariant() + ":", run.Stdout);
    }

    public static TheoryData<string, string, CascadeBehavior> CliEveryValidNameOfEveryOption()
    {
        var data = new TheoryData<string, string, CascadeBehavior>();
        var spelling = 0;
        foreach (var action in Six)
        {
            foreach (var value in CascadeActionRules.ValidFor(action))
            {
                var name = value.ToString();
                var text = (spelling++ % 3) switch { 0 => name, 1 => name.ToLowerInvariant(), _ => " " + name.ToUpperInvariant() + " " };
                data.Add("--cascade-" + action.ToLowerInvariant(), text, value);
            }
        }
        return data;
    }

    /// <summary>
    /// Every valid value of every option, through the real command and service on a mixed relationship: exactly that
    /// setting changes; the other seven are sent as read (or nothing is sent when the value is already current).
    /// </summary>
    [Theory]
    [MemberData(nameof(CliEveryValidNameOfEveryOption))]
    public async Task Cli_EveryValidValueOfEveryOption_ChangesExactlyThatSetting(string option, string text, CascadeBehavior value)
    {
        var stored = Relationship("mixed");
        var env = new FakeEnvironment(stored);
        var action = Six.Single(a => option == "--cascade-" + a.ToLowerInvariant());
        var index = Array.IndexOf(Eight, action);

        var run = await RunCliAsync(env, ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", option, text, "--output-format", "json"]);

        Assert.Equal(0, run.Exit);
        Assert.Single(env.Reads);
        var expected = Kinds["mixed"].ToArray();
        expected[index] = ToSdk(value);
        if (Kinds["mixed"][index] == ToSdk(value))
        {
            Assert.Empty(env.Sent);
            Assert.Equal(Kinds["mixed"], Values(stored.CascadeConfiguration));
        }
        else
        {
            var sent = Assert.Single(env.Sent);
            Assert.Same(stored, sent.Relationship);
            Assert.Equal(expected, Values(((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration));
        }
    }

    [Fact]
    public async Task Cli_NothingGiven_NamesOnlyTheOptionsTheCommandHas()
    {
        var env = new FakeEnvironment(Relationship("mixed"));

        var run = await RunCliAsync(env, ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", "--output-format", "json"]);

        Assert.Equal(ExitCodes.ValidationError, run.Exit);
        Assert.Equal(0, run.ProviderCalls);
        foreach (var action in Six)
            Assert.Contains("--cascade-" + action.ToLowerInvariant(), run.Stdout);
        Assert.DoesNotContain("hierarchical", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Reads);
    }

    [Fact]
    public async Task Cli_UnknownRelationship_ExitsNotFound()
    {
        var env = new FakeEnvironment(Relationship("mixed"))
        {
            ReadFailure = new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040217), Message = "synthetic" }, "synthetic")
        };

        var run = await RunCliAsync(env, ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", "--cascade-delete", "Restrict", "--output-format", "json"]);

        Assert.Equal(ExitCodes.NotFoundError, run.Exit);
        Assert.Equal(6, run.Exit);
        Assert.Contains(ErrorCodes.Operation.NotFound, run.Stdout);
        Assert.Empty(env.Sent);
    }

    // ---- Should-fix 7: the CLI output, text and JSON ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_TextOutput_ShowsBeforeAndAfter_AndTheInheritedAccessNotice(bool dryRun)
    {
        var env = new FakeEnvironment(Relationship("parental"));
        string[] args = ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", "--cascade-reparent", "NoCascade"];

        var run = await RunCliAsync(env, dryRun ? [.. args, "--dry-run"] : args);

        Assert.Equal(0, run.Exit);
        var lines = run.Stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("  Reparent  Cascade -> NoCascade", lines);
        foreach (var other in Six.Where(s => s != "Reparent"))
            Assert.Contains($"  {other,-9} Cascade -> Cascade (unchanged)", lines);
        Assert.Contains(RelationshipNotice(dryRun), lines);
        if (dryRun)
        {
            Assert.Empty(env.Sent);
            Assert.DoesNotContain(lines, l => l.EndsWith("to publish changes.", StringComparison.Ordinal));
        }
        else
        {
            Assert.Single(env.Sent);
            Assert.Contains("Relationship 'new_parent_child' updated. Run 'ppds publish --all' to publish changes.", lines);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_JsonOutput_PinsTheFlags(bool dryRun)
    {
        var env = new FakeEnvironment(Relationship("parental"));
        string[] args = ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", "--cascade-share", "NoCascade", "--output-format", "json"];

        var run = await RunCliAsync(env, dryRun ? [.. args, "--dry-run"] : args);

        Assert.Equal(0, run.Exit);
        using var doc = JsonDocument.Parse(run.Stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(!dryRun, data.GetProperty("updated").GetBoolean());
        Assert.Equal(dryRun, data.GetProperty("dryRun").GetBoolean());
        Assert.Equal(!dryRun, data.GetProperty("requiresPublish").GetBoolean());
        Assert.True(data.GetProperty("removesInheritedAccess").GetBoolean());
        Assert.False(data.GetProperty("nothingToChange").GetBoolean());
        Assert.Equal("new_parent_child", data.GetProperty("name").GetString());
        var share = data.GetProperty("cascadeSettings").EnumerateArray().Single(s => s.GetProperty("setting").GetString() == "Share");
        Assert.Equal(("Cascade", "NoCascade", true, true),
            (share.GetProperty("before").GetString(), share.GetProperty("after").GetString(), share.GetProperty("requested").GetBoolean(), share.GetProperty("changed").GetBoolean()));
        Assert.Equal(dryRun ? 0 : 1, env.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_ValueAlreadyCurrent_SendsNothing_AndSaysSo(bool json)
    {
        var env = new FakeEnvironment(Relationship("mixed")); // Delete is Restrict
        string[] args = ["relationship", "update", "--solution", "SyntheticSolution", "--name", "new_parent_child", "--cascade-delete", "Restrict"];

        var run = await RunCliAsync(env, json ? [.. args, "--output-format", "json"] : args);

        Assert.Equal(0, run.Exit);
        Assert.Single(env.Reads);
        Assert.Empty(env.Sent);
        if (json)
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("updated").GetBoolean());
            Assert.True(data.GetProperty("nothingToChange").GetBoolean());
            Assert.False(data.GetProperty("requiresPublish").GetBoolean());
        }
        else
        {
            Assert.Contains("Relationship 'new_parent_child' already has every value given; nothing was sent.", run.Stderr);
            Assert.DoesNotContain("updated.", run.Stderr);
        }
    }

    // ---- MF3: the survivors ----

    /// <summary>Two updates on one service instance: each reads the relationship; the second carries a change made in between.</summary>
    [Fact]
    public async Task TwoUpdatesOnOneService_EachReadsTheCurrentRelationship()
    {
        var env = new FakeEnvironment(Relationship("mixed"));

        await env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Cascade))));
        var changedInBetween = Relationship("mixed");
        changedInBetween.CascadeConfiguration.Merge = CascadeType.Cascade; // someone else changed Merge
        env.Stored = changedInBetween;
        await env.Service.UpdateRelationshipAsync(Request(Dto(("Assign", CascadeBehavior.NoCascade))));

        Assert.Equal(2, env.Reads.Count);
        Assert.Equal(2, env.Sent.Count);
        var second = (OneToManyRelationshipMetadata)env.Sent[1].Relationship;
        Assert.Same(changedInBetween, second);
        var expected = Kinds["mixed"].ToArray();
        expected[Array.IndexOf(Eight, "Merge")] = CascadeType.Cascade;
        expected[Array.IndexOf(Eight, "Assign")] = CascadeType.NoCascade;
        Assert.Equal(expected, Values(second.CascadeConfiguration));
    }

    public static TheoryData<string, int> EachActionAndAnUndefinedValue()
    {
        var data = new TheoryData<string, int>();
        foreach (var action in Six)
            foreach (var raw in new[] { -1, 6, 99, int.MaxValue })
                data.Add(action, raw);
        return data;
    }

    [Theory]
    [MemberData(nameof(EachActionAndAnUndefinedValue))]
    public async Task UndefinedValue_IsRefusedBeforeAnyRead(string action, int raw)
    {
        var env = new FakeEnvironment(Relationship("mixed"));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateRelationshipAsync(Request(Dto((action, (CascadeBehavior)raw)))));

        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Contains(CascadeActionRules.Describe(action), ex.Message);
        Assert.DoesNotContain(raw.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
        Assert.Empty(env.Reads);
        Assert.Empty(env.Sent);
    }

    /// <summary>The mapping to the SDK refuses an undefined value instead of writing NoCascade.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public void MappingToTheSdk_RefusesAnUndefinedValue(int raw)
    {
        var map = typeof(DataverseMetadataAuthoringService).GetMethod("ToSdkCascadeType", BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = Assert.Throws<TargetInvocationException>(() => map.Invoke(null, [(CascadeBehavior)raw]));

        Assert.IsType<MetadataValidationException>(ex.InnerException);
        foreach (var value in Enum.GetValues<CascadeBehavior>())
            Assert.Equal(ToSdk(value), (CascadeType)map.Invoke(null, [value])!);
    }

    // ---- Should-fix 3: the write's failures ----

    [Fact]
    public async Task WriteRefusedByDataverse_SaysNothingWasChanged_WithoutTheServerText()
    {
        var env = new FakeEnvironment(Relationship("mixed"))
        {
            WriteFailure = new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80048403), Message = "synthetic 0f8fad5b-d9cb-469f-a165-70867728950e" },
                "synthetic 0f8fad5b-d9cb-469f-a165-70867728950e")
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(() => env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Cascade)))));

        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Contains("refused the relationship update, so nothing was changed", ex.UserMessage);
        Assert.Contains("0x80048403", ex.UserMessage);
        Assert.DoesNotContain("0f8fad5b", ex.UserMessage);
        Assert.DoesNotContain("synthetic", ex.UserMessage);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("no-answer")]
    public async Task WriteWithoutAnAnswer_SaysTheOutcomeIsUnknown(string failure)
    {
        var env = new FakeEnvironment(Relationship("mixed"))
        {
            WriteFailure = failure switch
            {
                "timeout" => new TimeoutException("synthetic"),
                "cancelled" => new TaskCanceledException("synthetic"),
                _ => new InvalidOperationException("synthetic")
            }
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(() => env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Cascade)))));

        Assert.Contains("whether it was applied is unknown", ex.UserMessage);
        Assert.DoesNotContain("nothing was changed", ex.UserMessage);
        Assert.DoesNotContain("synthetic", ex.UserMessage);
        if (failure == "timeout")
            Assert.Equal(ExitCodes.ConnectionError, ExceptionMapper.ToExitCode(ex));
    }

    // ---- Should-fix 4: the read is the relationship asked for ----

    [Theory]
    [InlineData("another-name")]
    [InlineData("no-id")]
    [InlineData("empty-id")]
    public async Task ReadOfAnotherRelationship_IsNotWrittenBack(string fault)
    {
        var stored = Relationship("mixed");
        switch (fault)
        {
            case "another-name": stored.SchemaName = "new_other_child"; break;
            case "no-id": stored.MetadataId = null; break;
            case "empty-id": stored.MetadataId = Guid.Empty; break;
        }
        var env = new FakeEnvironment(stored);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Cascade)))));

        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Contains("nothing was changed", ex.UserMessage);
        Assert.DoesNotContain("new_other_child", ex.UserMessage);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task ReadNameDiffersOnlyInLetterCase_IsAccepted_AndTheStoredNameIsReported()
    {
        var stored = Relationship("mixed");
        stored.SchemaName = "new_Parent_Child";
        var env = new FakeEnvironment(stored);

        var result = await env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Cascade))));

        Assert.Equal("new_Parent_Child", result.SchemaName);
        Assert.Single(env.Sent);
    }

    // ---- Should-fix 5: nothing to change, nothing sent ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryValueGivenIsAlreadyCurrent_SendsNothing_AndSaysSo(bool dryRun)
    {
        var stored = Relationship("mixed");
        stored.IsHierarchical = true;
        var snapshot = Serialize(stored);
        var env = new FakeEnvironment(stored);

        var result = await env.Service.UpdateRelationshipAsync(new AuthoringUpdateRelationshipRequest
        {
            SolutionUniqueName = "SyntheticSolution",
            SchemaName = "new_parent_child",
            CascadeConfiguration = Dto(("Assign", CascadeBehavior.Active), ("Delete", CascadeBehavior.Restrict), ("Merge", CascadeBehavior.NoCascade)),
            IsHierarchical = true,
            DryRun = dryRun
        });

        Assert.Single(env.Reads);
        Assert.Empty(env.Sent);
        Assert.False(result.HasChanges);
        Assert.False(result.WasSent);
        Assert.All(result.CascadeSettings, s => Assert.False(s.Changed));
        Assert.Equal(snapshot, Serialize(stored));
    }

    [Fact]
    public async Task OnlyTheHierarchicalFlagChanges_IsSent()
    {
        var stored = Relationship("mixed");
        stored.IsHierarchical = false;
        var env = new FakeEnvironment(stored);

        var result = await env.Service.UpdateRelationshipAsync(new AuthoringUpdateRelationshipRequest
        {
            SolutionUniqueName = "SyntheticSolution",
            SchemaName = "new_parent_child",
            CascadeConfiguration = Dto(("Delete", CascadeBehavior.Restrict)), // already current
            IsHierarchical = true
        });

        Assert.True(result.HasChanges && result.WasSent);
        Assert.Single(env.Sent);
    }

    // ---- Helpers ----

    private static string RelationshipNotice(bool dryRun)
        => PPDS.Cli.Commands.Metadata.Relationship.RelationshipCommandGroup.InheritedAccessNotice(dryRun);

    private sealed record CliRun(int Exit, string Stdout, string Stderr, int ProviderCalls);

    private static async Task<CliRun> RunCliAsync(FakeEnvironment env, string[] args)
    {
        var calls = 0;
        Task<ServiceProvider> Provider(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new ServiceCollection()
                .AddSingleton<IMetadataAuthoringService>(env.Service)
                .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
                .BuildServiceProvider());
        }

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            exit = await MetadataCommandGroup.Create(Provider).Parse(args).InvokeAsync();
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }

        return new CliRun(exit, stdout.ToString(), stderr.ToString(), calls);
    }
}
