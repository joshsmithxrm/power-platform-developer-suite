using System.CommandLine;
using System.ServiceModel;
using System.Text.Json;
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
/// #1919: updating one cascade setting of a relationship changes that setting only. Every setting the caller did not
/// give is sent as read; a relationship whose current settings cannot be read is refused before anything is sent.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class RelationshipCascadeUpdateTests
{
    private static readonly string[] SettingNames = ["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"];

    [Fact]
    public async Task Parental_ChangeDeleteOnly_SendsTheOtherFiveAsRead()
    {
        var env = new FakeEnvironment(Parental());

        var result = await env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict }));

        var sent = env.SingleSentCascade();
        Assert.Equal(CascadeType.Restrict, sent.Delete);
        Assert.Equal(CascadeType.Cascade, sent.Assign);
        Assert.Equal(CascadeType.Cascade, sent.Merge);
        Assert.Equal(CascadeType.Cascade, sent.Reparent);
        Assert.Equal(CascadeType.Cascade, sent.Share);
        Assert.Equal(CascadeType.Cascade, sent.Unshare);
        Assert.Equal(0, ChangedButNotGiven(Parental(), sent, "Delete"));

        // The settings outside the six an update can give travel unchanged too.
        Assert.Equal(CascadeType.Cascade, sent.Archive);
        Assert.Equal(CascadeType.Cascade, sent.RollupView);

        Assert.False(result.WasDryRun);
        Assert.Equal(SettingNames, result.CascadeSettings.Select(s => s.Setting));
        var delete = Assert.Single(result.CascadeSettings, s => s.Changed);
        Assert.Equal(("Delete", CascadeBehavior.Cascade, CascadeBehavior.Restrict, true),
            (delete.Setting, delete.Before, delete.After, delete.Requested));
        Assert.All(result.CascadeSettings.Where(s => s.Setting != "Delete"), s =>
        {
            Assert.False(s.Requested);
            Assert.Equal(CascadeBehavior.Cascade, s.Before);
            Assert.Equal(CascadeBehavior.Cascade, s.After);
        });
    }

    [Fact]
    public async Task McpShape_DeleteAndAssign_ChangesExactlyThoseTwo()
    {
        var env = new FakeEnvironment(Parental());

        await env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto
        {
            Delete = CascadeBehavior.RemoveLink,
            Assign = CascadeBehavior.UserOwned
        }));

        var sent = env.SingleSentCascade();
        Assert.Equal(CascadeType.RemoveLink, sent.Delete);
        Assert.Equal(CascadeType.UserOwned, sent.Assign);
        Assert.Equal(0, ChangedButNotGiven(Parental(), sent, "Delete", "Assign"));
    }

    [Fact]
    public async Task ReadsAsIfPublished_AndKeepsOtherLanguages()
    {
        var env = new FakeEnvironment(Parental());

        await env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict }));

        var read = Assert.Single(env.Reads);
        Assert.True(read.RetrieveAsIfPublished);
        Assert.Equal("new_parent_child", read.Name);
        Assert.True(Assert.Single(env.Sent).MergeLabels);
    }

    [Fact]
    public async Task DryRun_ReadsAndReportsBeforeAndAfter_SendsNothing()
    {
        var env = new FakeEnvironment(Parental());

        var result = await env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict }, dryRun: true));

        Assert.Single(env.Reads);
        Assert.Empty(env.Sent);
        Assert.True(result.WasDryRun);
        var delete = Assert.Single(result.CascadeSettings, s => s.Changed);
        Assert.Equal((CascadeBehavior.Cascade, CascadeBehavior.Restrict), (delete.Before, delete.After));
    }

    public static TheoryData<string> UnreadableSettings => new() { "no-configuration", "Assign", "Delete", "Merge", "Reparent", "Share", "Unshare" };

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public async Task CurrentSettingNotRead_IsRefusedAndNothingIsSent(string missing)
    {
        var relationship = Parental();
        if (missing == "no-configuration")
            relationship.CascadeConfiguration = null;
        else
            typeof(CascadeConfiguration).GetProperty(missing)!.SetValue(relationship.CascadeConfiguration, null);
        var env = new FakeEnvironment(relationship);

        var ex = await Assert.ThrowsAsync<PpdsException>(() =>
            env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict })));

        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task ReadFails_IsRefusedAndNothingIsSent()
    {
        var env = new FakeEnvironment(Parental())
        {
            ReadFailure = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "synthetic" }, "synthetic")
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(() =>
            env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict })));

        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.DoesNotContain("synthetic", ex.UserMessage);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task ManyToMany_IsRefusedAndNothingIsSent()
    {
        var env = new FakeEnvironment(new ManyToManyRelationshipMetadata { SchemaName = "new_parent_child" });

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() =>
            env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto { Delete = CascadeBehavior.Restrict })));

        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Empty(env.Sent);
    }

    [Theory]
    [InlineData("Delete", CascadeBehavior.NoCascade)]
    [InlineData("Delete", CascadeBehavior.Active)]
    [InlineData("Delete", CascadeBehavior.UserOwned)]
    [InlineData("Merge", CascadeBehavior.Restrict)]
    [InlineData("Merge", CascadeBehavior.Active)]
    [InlineData("Assign", CascadeBehavior.Restrict)]
    [InlineData("Assign", CascadeBehavior.RemoveLink)]
    [InlineData("Reparent", CascadeBehavior.Restrict)]
    [InlineData("Share", CascadeBehavior.RemoveLink)]
    [InlineData("Unshare", CascadeBehavior.Restrict)]
    public async Task UndocumentedValueForTheAction_IsRefusedBeforeAnythingIsRead(string setting, CascadeBehavior value)
    {
        var env = new FakeEnvironment(Parental());
        var dto = new CascadeConfigurationDto();
        typeof(CascadeConfigurationDto).GetProperty(setting)!.SetValue(dto, value);

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateRelationshipAsync(Request(dto)));

        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Equal("CascadeConfiguration." + setting, ex.Field);
        Assert.Empty(env.Reads);
        Assert.Empty(env.Sent);
    }

    [Fact]
    public async Task NothingGiven_IsRefusedBeforeAnythingIsRead()
    {
        var env = new FakeEnvironment(Parental());

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() =>
            env.Service.UpdateRelationshipAsync(Request(new CascadeConfigurationDto())));

        Assert.Equal(MetadataErrorCodes.MissingRequiredField, ex.ErrorCode);
        Assert.Empty(env.Reads);
        Assert.Empty(env.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliShape_CascadeDeleteRestrict_SendsTheOtherFiveAsRead_AndReportsThem(bool dryRun)
    {
        var env = new FakeEnvironment(Parental());
        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton<IMetadataAuthoringService>(env.Service)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
            .BuildServiceProvider());
        var args = ("relationship update --solution SyntheticSolution --name new_parent_child --cascade-delete Restrict --output-format json"
            + (dryRun ? " --dry-run" : "")).Split(' ');

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            exit = await MetadataCommandGroup.Create(Provider).Parse(args).InvokeAsync();
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }

        Assert.Equal(0, exit);
        if (dryRun)
            Assert.Empty(env.Sent);
        else
            Assert.Equal(0, ChangedButNotGiven(Parental(), env.SingleSentCascade(), "Delete"));

        using var doc = JsonDocument.Parse(stdout.ToString());
        var settings = doc.RootElement.GetProperty("data").GetProperty("cascadeSettings").EnumerateArray()
            .ToDictionary(s => s.GetProperty("setting").GetString()!, s => (s.GetProperty("before").GetString(), s.GetProperty("after").GetString()));
        Assert.Equal(SettingNames, settings.Keys);
        Assert.Equal(("Cascade", "Restrict"), settings["Delete"]);
        Assert.All(SettingNames.Where(n => n != "Delete"), n => Assert.Equal(("Cascade", "Cascade"), settings[n]));
        if (dryRun)
            Assert.Equal("current-settings", doc.RootElement.GetProperty("metadata").GetProperty("preview").GetProperty("validationScope").GetString());
    }

    private static AuthoringUpdateRelationshipRequest Request(CascadeConfigurationDto cascade, bool dryRun = false) => new()
    {
        SolutionUniqueName = "SyntheticSolution",
        SchemaName = "new_parent_child",
        CascadeConfiguration = cascade,
        DryRun = dryRun
    };

    /// <summary>A parental relationship: every cascade setting is Cascade.</summary>
    private static OneToManyRelationshipMetadata Parental() => new()
    {
        SchemaName = "new_parent_child",
        ReferencedEntity = "new_parent",
        ReferencingEntity = "new_child",
        CascadeConfiguration = new CascadeConfiguration
        {
            Assign = CascadeType.Cascade,
            Delete = CascadeType.Cascade,
            Merge = CascadeType.Cascade,
            Reparent = CascadeType.Cascade,
            Share = CascadeType.Cascade,
            Unshare = CascadeType.Cascade,
            Archive = CascadeType.Cascade,
            RollupView = CascadeType.Cascade // not NoCascade, the default a reset would produce
        }
    };

    private static int ChangedButNotGiven(OneToManyRelationshipMetadata before, CascadeConfiguration sent, params string[] given)
        => SettingNames.Except(given).Count(name =>
            !Equals(typeof(CascadeConfiguration).GetProperty(name)!.GetValue(before.CascadeConfiguration),
                    typeof(CascadeConfiguration).GetProperty(name)!.GetValue(sent)));

    /// <summary>A recording fake of the pooled Dataverse client: answers the relationship read, records every update.</summary>
    private sealed class FakeEnvironment
    {
        private readonly RelationshipMetadataBase _stored;
        internal List<RetrieveRelationshipRequest> Reads { get; } = [];
        internal List<SdkUpdateRelationshipRequest> Sent { get; } = [];
        internal Exception? ReadFailure { get; init; }
        internal DataverseMetadataAuthoringService Service { get; }

        internal FakeEnvironment(RelationshipMetadataBase stored)
        {
            _stored = stored;
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
                                response.Results["RelationshipMetadata"] = _stored;
                                return Task.FromResult<OrganizationResponse>(response);
                            case SdkUpdateRelationshipRequest update:
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

        internal CascadeConfiguration SingleSentCascade()
            => Assert.IsType<OneToManyRelationshipMetadata>(Assert.Single(Sent).Relationship).CascadeConfiguration;
    }
}
