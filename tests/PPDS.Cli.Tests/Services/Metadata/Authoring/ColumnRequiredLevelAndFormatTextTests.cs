using System.CommandLine;
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

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// #1919, review round 2: required-level and format text that names nothing PPDS knows is refused before anything is
/// read; it is never read as a default (a typo used to send a required level of None, making a required column
/// optional, or a default format).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class ColumnRequiredLevelAndFormatTextTests
{
    public static TheoryData<string, bool> RequiredLevelTextThatNamesNoLevel()
    {
        var data = new TheoryData<string, bool>();
        foreach (var text in new[] { "Requird", "Mandatory", "", "   ", "2", "Required,None", "Application Required", "required." })
            foreach (var dryRun in new[] { false, true })
                data.Add(text, dryRun);
        return data;
    }

    [Theory]
    [MemberData(nameof(RequiredLevelTextThatNamesNoLevel))]
    public async Task Update_RequiredLevelTextThatNamesNoLevel_IsRefusedBeforeAnyRead(string text, bool dryRun)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.ApplicationRequired));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateColumnAsync(Update(r => r.RequiredLevel = text, dryRun)));

        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Equal("RequiredLevel", ex.Field);
        Assert.Contains("None, Recommended, Required, ApplicationRequired or SystemRequired", ex.Message);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Empty(env.Requests);
    }

    [Theory]
    [InlineData("Required", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData(" required ", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("ApplicationRequired", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("RECOMMENDED", AttributeRequiredLevel.Recommended)]
    [InlineData("none", AttributeRequiredLevel.None)]
    [InlineData("SystemRequired", AttributeRequiredLevel.SystemRequired)]
    public async Task Update_RequiredLevelName_IsSent(string text, AttributeRequiredLevel expected)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.Recommended));

        await env.Service.UpdateColumnAsync(Update(r => r.RequiredLevel = text));

        Assert.Equal(expected, env.SingleUpdate().RequiredLevel!.Value);
    }

    [Theory]
    [InlineData("Emial")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("Email,Url")]
    public async Task Update_FormatTextThatNamesNoFormat_IsRefusedBeforeAnyRead(string text)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.None));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateColumnAsync(Update(r => r.Format = text)));

        Assert.Equal("Format", ex.Field);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Empty(env.Requests);
    }

    public static TheoryData<string, string> FormatOfAnotherColumnType() => new()
    {
        { "String", "Duration" }, { "String", "DateOnly" }, { "Integer", "Email" }, { "Integer", "DateAndTime" },
        { "DateTime", "Json" }, { "DateTime", "None" },
    };

    [Theory]
    [MemberData(nameof(FormatOfAnotherColumnType))]
    public async Task Update_FormatTheColumnTypeDoesNotTake_IsRefusedBeforeTheWrite(string type, string text)
    {
        var env = new FakeColumnEnvironment(Column(type));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.UpdateColumnAsync(Update(r => r.Format = text)));

        Assert.Equal("Format", ex.Field);
        Assert.Contains($"a {type} column takes", ex.Message);
        Assert.IsType<RetrieveAttributeRequest>(Assert.Single(env.Requests));
    }

    [Theory]
    [InlineData("String", "email", "Email")]
    [InlineData("String", " Phonetic ", "PhoneticGuide")]
    [InlineData("String", "TEXT", "Text")]
    [InlineData("Integer", " TimeZone ", "TimeZone")]
    [InlineData("Integer", "none", "None")]
    [InlineData("DateTime", "dateonly", "DateOnly")]
    [InlineData("DateTime", "DateAndTime", "DateAndTime")]
    public async Task Update_FormatName_IsSent(string type, string text, string expected)
    {
        var env = new FakeColumnEnvironment(Column(type));

        await env.Service.UpdateColumnAsync(Update(r => r.Format = text));

        var sent = env.SingleUpdate();
        var actual = sent switch
        {
            StringAttributeMetadata s => s.FormatName!.Value,
            IntegerAttributeMetadata i => i.Format.ToString(),
            DateTimeAttributeMetadata d => d.Format.ToString(),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, actual);
    }

    public static TheoryData<SchemaColumnType, string?, string?> CreateTextThatNamesNothing() => new()
    {
        { SchemaColumnType.String, "Requird", null },
        { SchemaColumnType.Integer, "2", null },
        { SchemaColumnType.String, null, "Emial" },
        { SchemaColumnType.String, null, "Duration" },
        { SchemaColumnType.Integer, null, "Durration" },
        { SchemaColumnType.Integer, null, "Email" },
        { SchemaColumnType.DateTime, null, "DateOnyl" },
        { SchemaColumnType.DateTime, null, "Json" },
    };

    [Theory]
    [MemberData(nameof(CreateTextThatNamesNothing))]
    public async Task Create_TextThatNamesNothing_IsRefusedBeforeAnyRead(SchemaColumnType type, string? requiredLevel, string? format)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.None));

        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => env.Service.CreateColumnAsync(new CreateColumnRequest
        {
            SolutionUniqueName = "SyntheticSolution",
            EntityLogicalName = "new_table",
            SchemaName = "new_Thing",
            DisplayName = "Thing",
            ColumnType = type,
            RequiredLevel = requiredLevel,
            Format = format
        }));

        Assert.Equal(requiredLevel != null ? "RequiredLevel" : "Format", ex.Field);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Empty(env.Requests);
    }

    // ---- Through the surfaces that pass the text: the attribute command, its column alias ----

    [Theory]
    [InlineData("attribute")]
    [InlineData("column")]
    public async Task Cli_UpdateWithARequiredLevelTypo_ExitsValidationError_AndReadsNothing(string group)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.ApplicationRequired));

        var (exit, stdout) = await RunCliAsync(env,
            [group, "update", "--solution", "SyntheticSolution", "--entity", "new_table", "--column", "new_thing", "--required-level", "Requird", "--output-format", "json"]);

        Assert.Equal(ExitCodes.ValidationError, exit);
        Assert.Contains("None, Recommended, Required, ApplicationRequired or SystemRequired", stdout);
        Assert.DoesNotContain("Requird", stdout);
        Assert.Empty(env.Requests);
    }

    [Theory]
    [InlineData("attribute", "--format", "Emial")]
    [InlineData("column", "--format", "Emial")]
    [InlineData("attribute", "--required-level", "Mandatory")]
    public async Task Cli_CreateWithATypo_ExitsValidationError_AndReadsNothing(string group, string option, string text)
    {
        var env = new FakeColumnEnvironment(StringColumn(AttributeRequiredLevel.None));

        var (exit, _) = await RunCliAsync(env,
            [group, "create", "--solution", "SyntheticSolution", "--entity", "new_table", "--name", "new_Thing", "--display-name", "Thing", "--type", "String", option, text, "--output-format", "json"]);

        Assert.Equal(ExitCodes.ValidationError, exit);
        Assert.Empty(env.Requests);
    }

    // ---- Helpers ----

    private static UpdateColumnRequest Update(Action<UpdateColumnRequest> set, bool dryRun = false)
    {
        var request = new UpdateColumnRequest
        {
            SolutionUniqueName = "SyntheticSolution",
            EntityLogicalName = "new_table",
            ColumnLogicalName = "new_thing",
            DryRun = dryRun
        };
        set(request);
        return request;
    }

    private static AttributeMetadata StringColumn(AttributeRequiredLevel level) => new StringAttributeMetadata
    {
        LogicalName = "new_thing",
        SchemaName = "new_Thing",
        MaxLength = 100,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(level)
    };

    private static AttributeMetadata Column(string type) => type switch
    {
        "String" => StringColumn(AttributeRequiredLevel.None),
        "Integer" => new IntegerAttributeMetadata { LogicalName = "new_thing", SchemaName = "new_Thing" },
        "DateTime" => new DateTimeAttributeMetadata { LogicalName = "new_thing", SchemaName = "new_Thing" },
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static async Task<(int Exit, string Stdout)> RunCliAsync(FakeColumnEnvironment env, string[] args)
    {
        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton<IMetadataAuthoringService>(env.Service)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
            .BuildServiceProvider());

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var exit = await MetadataCommandGroup.Create(Provider).Parse(args).InvokeAsync();
            return (exit, stdout.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    /// <summary>A recording fake of the pooled client: answers the column read, records every request.</summary>
    private sealed class FakeColumnEnvironment
    {
        internal List<OrganizationRequest> Requests { get; } = [];
        internal DataverseMetadataAuthoringService Service { get; }

        internal FakeColumnEnvironment(AttributeMetadata stored)
        {
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    Requests.Add(request);
                    if (request is RetrieveAttributeRequest)
                    {
                        var response = new RetrieveAttributeResponse();
                        response.Results["AttributeMetadata"] = stored;
                        return Task.FromResult<OrganizationResponse>(response);
                    }
                    return Task.FromResult(new OrganizationResponse());
                });
            client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<Microsoft.Xrm.Sdk.Query.QueryBase, CancellationToken>((query, _) =>
                {
                    Requests.Add(new OrganizationRequest("RetrieveMultiple"));
                    return Task.FromResult(new EntityCollection());
                });
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
            guard.Setup(g => g.EnsureCanMutate(It.IsAny<string>()));
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), guard.Object,
                baseLanguage: new FixedOrganizationBaseLanguage(1033));
        }

        internal AttributeMetadata SingleUpdate()
            => Assert.Single(Requests.OfType<UpdateAttributeRequest>()).Attribute;
    }
}
