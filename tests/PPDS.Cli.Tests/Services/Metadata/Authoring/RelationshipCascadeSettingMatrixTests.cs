using System.CommandLine;
using System.ServiceModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using AuthoringUpdateRelationshipRequest = PPDS.Dataverse.Metadata.Authoring.UpdateRelationshipRequest;
using SdkUpdateRelationshipRequest = Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// #1919, review round 1: every one of the six cascade settings, given alone or with others, on several kinds of
/// relationship, changes only what was given. All eight values of the configuration sent are checked (the six an
/// update can give, plus Archive and RollupView), with fixture values that differ from every default, so a reset of
/// any of them is visible. The relationship sent is the object read, so its other properties travel as read.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed partial class RelationshipCascadeSettingMatrixTests
{
    private static readonly string[] Six = ["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"];
    private static readonly string[] Eight = [.. Six, "Archive", "RollupView"];

    /// <summary>
    /// Relationship kinds, as the eight values of their cascade configuration in the order of <see cref="Eight"/>.
    /// Archive and RollupView are never NoCascade, the default a reset would produce.
    /// </summary>
    private static readonly Dictionary<string, CascadeType[]> Kinds = new()
    {
        ["parental"] = [CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade],
        ["mixed"] = [CascadeType.Active, CascadeType.Restrict, CascadeType.NoCascade, CascadeType.UserOwned, CascadeType.Cascade, CascadeType.NoCascade, CascadeType.RemoveLink, CascadeType.Active],
        ["referential"] = [CascadeType.NoCascade, CascadeType.RemoveLink, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.Cascade, CascadeType.Cascade],
        ["restrict"] = [CascadeType.NoCascade, CascadeType.Restrict, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.Restrict, CascadeType.UserOwned],
        ["user-owned"] = [CascadeType.UserOwned, CascadeType.Cascade, CascadeType.Cascade, CascadeType.UserOwned, CascadeType.UserOwned, CascadeType.UserOwned, CascadeType.Cascade, CascadeType.Restrict],
        ["active"] = [CascadeType.Active, CascadeType.RemoveLink, CascadeType.NoCascade, CascadeType.Active, CascadeType.Active, CascadeType.Active, CascadeType.Active, CascadeType.RemoveLink],
    };

    public static TheoryData<string, string> EachSettingAloneOnParentalAndMixed()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in new[] { "parental", "mixed" })
            foreach (var setting in Six)
                data.Add(kind, setting);
        return data;
    }

    [Theory]
    [MemberData(nameof(EachSettingAloneOnParentalAndMixed))]
    public async Task EachSettingGivenAlone_ChangesThatSettingOnly_AllEightValuesChecked(string kind, string setting)
    {
        var stored = Relationship(kind);
        var env = new FakeEnvironment(stored);
        var newValue = NewValue(kind, setting);

        var result = await env.Service.UpdateRelationshipAsync(Request(Dto((setting, newValue))));

        var sent = Assert.Single(env.Sent);
        Assert.Same(stored, sent.Relationship); // the object read is the object sent: its other properties travel as read
        Assert.Equal("SyntheticSolution", sent.SolutionUniqueName);
        Assert.True(sent.MergeLabels);
        var expected = Kinds[kind].ToArray();
        expected[Array.IndexOf(Eight, setting)] = ToSdk(newValue);
        Assert.Equal(expected, Values(((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration));
        AssertResultMatches(result, kind, ((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration, [setting]);
    }

    [Fact]
    public async Task EverySubsetOfTheSix_OnSixKinds_SendsExactlyTheGivenChanges()
    {
        var cases = 0;
        foreach (var kind in Kinds.Keys)
        {
            for (var mask = 1; mask < 1 << Six.Length; mask++)
            {
                var given = Six.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
                var stored = Relationship(kind);
                var env = new FakeEnvironment(stored);
                var values = given.Select((s, i) => (s, NewValue(kind, s, rotation: mask + i))).ToArray();

                var result = await env.Service.UpdateRelationshipAsync(Request(Dto(values)));

                var sent = Assert.Single(env.Sent);
                Assert.Same(stored, sent.Relationship);
                var expected = Kinds[kind].ToArray();
                foreach (var (s, v) in values)
                    expected[Array.IndexOf(Eight, s)] = ToSdk(v);
                var sentCascade = ((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration;
                Assert.True(expected.SequenceEqual(Values(sentCascade)), $"{kind} given {string.Join("+", given)}");
                AssertResultMatches(result, kind, sentCascade, given);
                cases++;
            }
        }
        Assert.Equal(6 * 63, cases);
    }

    [Fact]
    public async Task OtherPropertiesOfTheRelationship_AreSentAsRead()
    {
        var stored = Relationship("parental");
        stored.ReferencedEntityNavigationPropertyName = "new_children";
        stored.ReferencingEntityNavigationPropertyName = "new_parentid";
        stored.IsCustomizable = new BooleanManagedProperty(false);
        stored.AssociatedMenuConfiguration = new AssociatedMenuConfiguration
        {
            Behavior = AssociatedMenuBehavior.UseLabel,
            Group = AssociatedMenuGroup.Sales,
            Order = 42,
            Label = new Label(new LocalizedLabel("Children", 1033), new[] { new LocalizedLabel("Children", 1033), new LocalizedLabel("Enfants", 1036) })
        };
        var snapshot = Serialize(stored);
        var env = new FakeEnvironment(stored);

        await env.Service.UpdateRelationshipAsync(Request(Dto(("Share", CascadeBehavior.UserOwned))));

        var sent = (OneToManyRelationshipMetadata)Assert.Single(env.Sent).Relationship;
        Assert.Same(stored, sent);
        // Restore the one setting given; the serialised definition must then equal the one read.
        sent.CascadeConfiguration.Share = CascadeType.Cascade;
        Assert.Equal(snapshot, Serialize(sent));
    }

    public static TheoryData<string, CascadeBehavior> EveryActionAndValue()
    {
        var data = new TheoryData<string, CascadeBehavior>();
        foreach (var action in Six)
            foreach (var value in Enum.GetValues<CascadeBehavior>())
                data.Add(action, value);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryActionAndValue))]
    public async Task EachDocumentedValueIsAccepted_EachOtherIsRefusedBeforeAnyRead(string action, CascadeBehavior value)
    {
        var documented = action switch
        {
            "Delete" => new[] { CascadeBehavior.Cascade, CascadeBehavior.RemoveLink, CascadeBehavior.Restrict },
            "Merge" => [CascadeBehavior.Cascade, CascadeBehavior.NoCascade],
            _ => [CascadeBehavior.Active, CascadeBehavior.Cascade, CascadeBehavior.NoCascade, CascadeBehavior.UserOwned]
        };
        var env = new FakeEnvironment(Relationship("mixed"));

        if (documented.Contains(value))
        {
            var result = await env.Service.UpdateRelationshipAsync(Request(Dto((action, value))));
            if (Kinds["mixed"][Array.IndexOf(Eight, action)] == ToSdk(value))
            {
                // Already the current value: nothing to change, nothing sent.
                Assert.Empty(env.Sent);
                Assert.False(result.WasSent || result.HasChanges);
            }
            else
            {
                var sent = ((OneToManyRelationshipMetadata)Assert.Single(env.Sent).Relationship).CascadeConfiguration;
                Assert.Equal(ToSdk(value), Values(sent)[Array.IndexOf(Eight, action)]);
                Assert.True(result.WasSent && result.HasChanges);
            }
        }
        else
        {
            var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateRelationshipAsync(Request(Dto((action, value)))));
            Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
            Assert.Equal("CascadeConfiguration." + action, ex.Field);
            foreach (var valid in documented)
                Assert.Contains(valid.ToString(), ex.Message);
            Assert.Empty(env.Reads);
            Assert.Empty(env.Sent);
        }
        Assert.Equal(documented, CascadeActionRules.ValidFor(action));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HierarchicalFlagAlone_IsAccepted_SendsTheCascadeAsRead_AndIsReported(bool flag)
    {
        var stored = Relationship("mixed");
        stored.IsHierarchical = !flag;
        var env = new FakeEnvironment(stored);

        var result = await env.Service.UpdateRelationshipAsync(new AuthoringUpdateRelationshipRequest
        {
            SolutionUniqueName = "SyntheticSolution", SchemaName = "new_parent_child", IsHierarchical = flag
        });

        var sent = (OneToManyRelationshipMetadata)Assert.Single(env.Sent).Relationship;
        Assert.Same(stored, sent);
        Assert.Equal(flag, sent.IsHierarchical);
        Assert.Equal(Kinds["mixed"], Values(sent.CascadeConfiguration));
        Assert.Equal((!flag, flag), (result.IsHierarchicalBefore, result.IsHierarchicalAfter));
        Assert.All(result.CascadeSettings, s => Assert.False(s.Changed || s.Requested));
    }

    [Fact]
    public async Task NothingGiven_RefusalSaysWhatToGive()
    {
        var env = new FakeEnvironment(Relationship("mixed"));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto())));

        foreach (var action in Six)
            Assert.Contains(action, ex.Message);
        Assert.Contains("hierarchical", ex.Message);
        Assert.Empty(env.Reads);
    }

    [Fact]
    public async Task UnknownRelationship_IsNotFound_AndSaysToCheckTheName()
    {
        var env = new FakeEnvironment(Relationship("mixed"))
        {
            ReadFailure = new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040217), Message = "Could not find relationship new_parent_child" },
                "Could not find relationship new_parent_child")
        };

        // The not-found exception type, so every surface maps it as not found (the CLI exits 6).
        var ex = await Assert.ThrowsAsync<PpdsNotFoundException>(() => env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Restrict)))));

        Assert.Equal(ErrorCodes.Operation.NotFound, ex.ErrorCode);
        Assert.Equal(ExitCodes.NotFoundError, ExceptionMapper.ToExitCode(ex));
        Assert.Contains("Check the schema name", ex.UserMessage);
        Assert.DoesNotContain("new_parent_child", ex.UserMessage);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task UnreadableRelationship_SaysWhatToCheck()
    {
        var env = new FakeEnvironment(Relationship("mixed"))
        {
            ReadFailure = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220), Message = "synthetic" }, "synthetic")
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(() => env.Service.UpdateRelationshipAsync(Request(Dto(("Delete", CascadeBehavior.Restrict)))));

        Assert.NotEqual(ErrorCodes.Operation.NotFound, ex.ErrorCode);
        Assert.Contains("Check the schema name and that your account can read", ex.UserMessage);
        Assert.DoesNotContain("synthetic", ex.UserMessage);
        Assert.Empty(env.Sent);
    }

    [Theory]
    [InlineData("parental", "Reparent", CascadeBehavior.NoCascade, true)]
    [InlineData("parental", "Share", CascadeBehavior.NoCascade, true)]
    [InlineData("parental", "Assign", CascadeBehavior.NoCascade, false)]
    [InlineData("parental", "Unshare", CascadeBehavior.NoCascade, false)]
    [InlineData("parental", "Reparent", CascadeBehavior.UserOwned, false)]
    [InlineData("referential", "Reparent", CascadeBehavior.NoCascade, false)] // already NoCascade: nothing changes
    [InlineData("referential", "Share", CascadeBehavior.Cascade, false)]
    public async Task ReparentOrShareChangedToNoCascade_IsReportedAsRemovingInheritedAccess(string kind, string setting, CascadeBehavior value, bool expected)
    {
        var env = new FakeEnvironment(Relationship(kind));

        var result = await env.Service.UpdateRelationshipAsync(Request(Dto((setting, value))));

        Assert.Equal(expected, result.RemovesInheritedAccess);
    }

    public static TheoryData<string, string> EachCliOption() => new()
    {
        { "--cascade-assign", "UserOwned" }, { "--cascade-delete", "Restrict" }, { "--cascade-merge", "NoCascade" },
        { "--cascade-reparent", "Active" }, { "--cascade-share", "UserOwned" }, { "--cascade-unshare", "Active" },
    };

    [Theory]
    [MemberData(nameof(EachCliOption))]
    public async Task Cli_EachCascadeOption_RequestCarriesExactlyThatSetting(string option, string value)
    {
        AuthoringUpdateRelationshipRequest? captured = null;
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.Setup(s => s.UpdateRelationshipAsync(It.IsAny<AuthoringUpdateRelationshipRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Callback((AuthoringUpdateRelationshipRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken _) => captured = r)
            .ReturnsAsync(new UpdateRelationshipResult { SchemaName = "new_parent_child" });
        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton(service.Object)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
            .BuildServiceProvider());

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            exit = await MetadataCommandGroup.Create(Provider)
                .Parse($"relationship update --solution SyntheticSolution --name new_parent_child {option} {value} --output-format json".Split(' '))
                .InvokeAsync();
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }

        Assert.Equal(0, exit);
        var cascade = Assert.IsType<CascadeConfigurationDto>(captured!.CascadeConfiguration);
        var setting = option["--cascade-".Length..];
        foreach (var name in Six)
        {
            var actual = (CascadeBehavior?)typeof(CascadeConfigurationDto).GetProperty(name)!.GetValue(cascade);
            if (string.Equals(name, setting, StringComparison.OrdinalIgnoreCase))
                Assert.Equal(Enum.Parse<CascadeBehavior>(value), actual);
            else
                Assert.Null(actual);
        }
        Assert.Null(captured.IsHierarchical);
    }

    [Fact]
    public void Cli_OptionHelp_NamesTheValuesValidForEachAction()
    {
        var command = MetadataCommandGroup.Create(_ => throw new InvalidOperationException("Help must not connect."));
        var update = command.Subcommands.Single(c => c.Name == "relationship").Subcommands.Single(c => c.Name == "update");
        foreach (var action in Six)
        {
            var help = update.Options.Single(o => o.Name == "--cascade-" + action.ToLowerInvariant()).Description!;
            foreach (var value in Enum.GetValues<CascadeBehavior>())
            {
                if (CascadeActionRules.IsValid(action, value)) Assert.Contains(value.ToString(), help);
                else Assert.DoesNotContain(value.ToString(), help);
            }
        }
        Assert.Contains("last writer wins", update.Description);
    }

    private static void AssertResultMatches(UpdateRelationshipResult result, string kind, CascadeConfiguration sent, string[] given)
    {
        Assert.Equal(Six, result.CascadeSettings.Select(s => s.Setting));
        foreach (var s in result.CascadeSettings)
        {
            var i = Array.IndexOf(Eight, s.Setting);
            Assert.Equal(Kinds[kind][i], ToSdk(s.Before));
            Assert.Equal(Values(sent)[i], ToSdk(s.After));
            Assert.Equal(given.Contains(s.Setting), s.Requested);
            Assert.Equal(Kinds[kind][i] != Values(sent)[i], s.Changed);
        }
    }

    /// <summary>
    /// A valid value for <paramref name="setting"/> that differs from its current value and, where one exists, from
    /// every other value of the configuration, so a write that lands on the wrong setting is visible.
    /// </summary>
    private static CascadeBehavior NewValue(string kind, string setting, int rotation = 0)
    {
        var current = Kinds[kind];
        var own = current[Array.IndexOf(Eight, setting)];
        var candidates = CascadeActionRules.ValidFor(setting).Where(v => ToSdk(v) != own).ToArray();
        var distinct = candidates.Where(v => !current.Contains(ToSdk(v))).ToArray();
        var pool = distinct.Length > 0 ? distinct : candidates;
        return pool[rotation % pool.Length];
    }

    private static CascadeConfigurationDto Dto(params (string Setting, CascadeBehavior Value)[] values)
    {
        var dto = new CascadeConfigurationDto();
        foreach (var (setting, value) in values)
            typeof(CascadeConfigurationDto).GetProperty(setting)!.SetValue(dto, value);
        return dto;
    }

    private static AuthoringUpdateRelationshipRequest Request(CascadeConfigurationDto cascade) => new()
    {
        SolutionUniqueName = "SyntheticSolution",
        SchemaName = "new_parent_child",
        CascadeConfiguration = cascade
    };

    private static OneToManyRelationshipMetadata Relationship(string kind)
    {
        var v = Kinds[kind];
        return new OneToManyRelationshipMetadata
        {
            SchemaName = "new_parent_child", MetadataId = new Guid("6f1d7c3e-2a4b-4c5d-8e9f-0a1b2c3d4e5f"),
            ReferencedEntity = "new_parent",
            ReferencingEntity = "new_child",
            CascadeConfiguration = new CascadeConfiguration
            {
                Assign = v[0], Delete = v[1], Merge = v[2], Reparent = v[3], Share = v[4], Unshare = v[5], Archive = v[6], RollupView = v[7]
            }
        };
    }

    /// <summary>The eight values; a value that is not set shows as -1, which no fixture or expectation uses.</summary>
    private static CascadeType[] Values(CascadeConfiguration c)
        => new[] { c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare, c.Archive, c.RollupView }
            .Select(v => v ?? (CascadeType)(-1)).ToArray();

    private static CascadeType ToSdk(CascadeBehavior value) => value switch
    {
        CascadeBehavior.Cascade => CascadeType.Cascade,
        CascadeBehavior.Active => CascadeType.Active,
        CascadeBehavior.NoCascade => CascadeType.NoCascade,
        CascadeBehavior.UserOwned => CascadeType.UserOwned,
        CascadeBehavior.RemoveLink => CascadeType.RemoveLink,
        CascadeBehavior.Restrict => CascadeType.Restrict,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Serialize(OneToManyRelationshipMetadata relationship)
    {
        var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(OneToManyRelationshipMetadata));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, relationship);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A recording fake of the pooled Dataverse client: answers the relationship read, records every update.</summary>
    private sealed class FakeEnvironment
    {
        internal List<RetrieveRelationshipRequest> Reads { get; } = [];
        internal List<SdkUpdateRelationshipRequest> Sent { get; } = [];
        internal Exception? ReadFailure { get; init; }
        internal Exception? WriteFailure { get; init; }
        /// <summary>What the environment holds; each read answers with the current value, so it can change between updates.</summary>
        internal RelationshipMetadataBase Stored { get; set; }
        internal DataverseMetadataAuthoringService Service { get; }

        internal FakeEnvironment(RelationshipMetadataBase stored)
        {
            Stored = stored;
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                var client = new Mock<IPooledClient>(MockBehavior.Strict);
                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                    {
                        switch (request)
                        {
                            case RetrieveRelationshipRequest read:
                                Reads.Add(read);
                                if (ReadFailure != null) return Task.FromException<OrganizationResponse>(ReadFailure);
                                var response = new RetrieveRelationshipResponse();
                                response.Results["RelationshipMetadata"] = Stored;
                                return Task.FromResult<OrganizationResponse>(response);
                            case SdkUpdateRelationshipRequest update:
                                if (WriteFailure != null) return Task.FromException<OrganizationResponse>(WriteFailure);
                                Sent.Add(update);
                                return Task.FromResult(new OrganizationResponse());
                            default:
                                throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName);
                        }
                    });
                return client.Object;
            });
            var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
            guard.Setup(g => g.EnsureCanMutate(It.IsAny<string>()));
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), guard.Object,
                baseLanguage: new FixedOrganizationBaseLanguage(1033));
        }
    }
}
