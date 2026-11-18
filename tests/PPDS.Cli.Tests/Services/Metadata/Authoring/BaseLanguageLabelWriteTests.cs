using System.Collections;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkCreateOneToMany = Microsoft.Xrm.Sdk.Messages.CreateOneToManyRequest;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkUpdateState = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Every label the authoring service writes is written in the organisation's base language (here 1036, as in the
/// French Test organisation, where 1033 labels were silently ignored, written over the base label or rejected), and
/// every label write is confirmed by an as-if-published read-back: a label Dataverse did not store, or a read-back that
/// fails, is WRITE_NOT_CONFIRMED and publication is not attempted (specs/metadata-authoring.md "Label languages",
/// AC-142, AC-145, AC-147, AC-148).
/// </summary>
[Trait("Category", "Unit")]
public sealed class BaseLanguageLabelWriteTests
{
    private const int French = 1036;
    private const int English = 1033;

    /// <summary>Every label-writing call of the service, with the texts it writes.</summary>
    private static readonly string[] AllPaths =
    [
        "create-table", "update-table", "create-column", "create-choice-column", "create-boolean-column", "update-column",
        "update-finite-column", "create-one-to-many", "create-choice", "update-choice", "add-option", "update-option",
        "add-statusreason", "update-statusreason", "add-column-option", "update-column-option", "update-state-value", "create-key"
    ];

    /// <summary>Paths that accept --publish (the read-back sits between the write and the publish).</summary>
    private static readonly string[] Publishing =
    [
        "create-table", "update-table", "create-column", "update-column", "update-finite-column", "add-statusreason",
        "update-statusreason", "add-column-option", "update-column-option"
    ];

    public static TheoryData<string> Paths => new(AllPaths);
    public static TheoryData<string> PublishingPaths => new(Publishing);

    [Theory]
    [MemberData(nameof(Paths))]
    [Trait("AC", "AC-142")]
    public async Task EveryLabel_IsWrittenInTheBaseLanguageOnly(string path)
    {
        var world = new World(French);
        await world.RunAsync(path);

        var labels = world.Writes.SelectMany(LabelsIn).Where(l => l.LocalizedLabels.Count > 0).ToList();
        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.All(label.LocalizedLabels, l => Assert.Equal(French, l.LanguageCode)));
        var texts = labels.SelectMany(l => l.LocalizedLabels).Select(l => l.Label).ToHashSet();
        Assert.All(World.Texts(path), text => Assert.Contains(text, texts));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    [Trait("AC", "AC-148")]
    public async Task EveryLabelWrite_IsReadBackOnce_AsIfPublished(string path)
    {
        var world = new World(French);
        await world.RunAsync(path);
        var expected = path == "create-table" ? 2 : 1; // the table, then its primary name column
        Assert.Equal(expected, world.ReadBacks.Count);
        Assert.All(world.ReadBacks, r => Assert.True(LabelWriteStore.IsReadBack(r)));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    [Trait("AC", "AC-148")]
    public async Task ALabelDataverseDidNotStore_IsWriteNotConfirmed(string path)
    {
        var world = new World(French) { IgnoreWrites = true };
        var error = await Assert.ThrowsAsync<PpdsException>(() => world.RunAsync(path));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        if (path is "add-option" or "add-statusreason" or "add-column-option")
            Assert.Contains($"could not be confirmed: Option {(path == "add-option" ? 100000005 : 7)} not found", error.Message); // nothing stored: the inserted option is missing
        else
        {
            Assert.Contains("Dataverse did not store", error.Message);
            Assert.Contains($"(LCID {French})", error.Message);
        }
        Assert.Contains("re-running the command", error.Message);
        Assert.DoesNotContain(world.Requests, r => r is PublishXmlRequest);
    }

    [Theory]
    [MemberData(nameof(PublishingPaths))]
    [Trait("AC", "AC-148")]
    public async Task ALabelDataverseDidNotStore_WithPublish_SaysPublicationWasNotAttempted(string path)
    {
        var world = new World(French) { IgnoreWrites = true, Publish = true };
        var error = await Assert.ThrowsAsync<PpdsException>(() => world.RunAsync(path));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        Assert.Contains("Publication (--publish) was not attempted.", error.Message);
        Assert.DoesNotContain(world.Requests, r => r is PublishXmlRequest);
    }

    [Theory]
    [MemberData(nameof(PublishingPaths))]
    [Trait("AC", "AC-148")]
    public async Task AConfirmedWrite_WithPublish_PublishesAfterTheReadBack(string path)
    {
        var world = new World(French) { Publish = true };
        await world.RunAsync(path);
        var all = world.All;
        var publish = all.FindIndex(r => r is PublishXmlRequest);
        Assert.True(publish > 0, "a confirmed write is published");
        Assert.True(all.FindLastIndex(r => world.ReadBacks.Contains(r)) < publish, "the read-back comes before the publish");
    }

    public static TheoryData<string, string> ReadBackFailures()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in AllPaths)
            foreach (var failure in new[] { "timeout", "fault", "cancelled" })
                data.Add(path, failure);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReadBackFailures))]
    [Trait("AC", "AC-148")]
    public async Task AFailedReadBack_IsWriteNotConfirmed_AndKeepsTheCause(string path, string failure)
    {
        Exception cause = failure switch
        {
            "timeout" => new TimeoutException("synthetic"),
            "cancelled" => new OperationCanceledException("synthetic, not the caller's token"),
            _ => new System.ServiceModel.FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147220970, Message = "synthetic" }, "synthetic")
        };
        var world = new World(French) { ReadBackFailure = cause, Publish = Publishing.Contains(path) };
        var error = await Assert.ThrowsAsync<PpdsException>(() => world.RunAsync(path));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        Assert.Contains("could not be confirmed", error.Message);
        for (Exception? inner = error.InnerException; ; inner = inner.InnerException)
        {
            Assert.NotNull(inner);
            if (ReferenceEquals(inner, cause)) break;
        }
        Assert.DoesNotContain(world.Requests, r => r is PublishXmlRequest);
    }

    [Theory]
    [InlineData("update-table")]
    [InlineData("create-column")]
    [InlineData("update-choice")]
    [InlineData("update-option")]
    [InlineData("update-state-value")]
    [InlineData("create-key")]
    [Trait("AC", "AC-148")]
    public async Task AReadBackThatNeedsASignIn_KeepsTheReauthenticationSignal(string path)
    {
        var world = new World(French) { ReadBackFailure = new PPDS.Dataverse.Resilience.DataverseAuthenticationException("synthetic", requiresReauthentication: true) };
        var error = await Assert.ThrowsAsync<PpdsException>(() => world.RunAsync(path));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        Assert.Equal(true, error.Context!["requiresReauthentication"]);
    }

    [Fact]
    [Trait("AC", "AC-148")]
    public async Task OptionLabelAndColour_AreConfirmedByOneRead()
    {
        var world = new World(French);
        await world.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            OptionSetName = World.Choice, Value = 1, NewLabel = "Fermé", Color = "#112233", SolutionUniqueName = World.Solution
        });
        Assert.Single(world.ReadBacks);
        var update = Assert.Single(world.Writes.OfType<SdkUpdateOption>());
        Assert.Equal("#112233", update["Color"]);
    }

    [Fact]
    [Trait("AC", "AC-148")]
    public async Task ColourOnly_IsStillConfirmed_AndAMismatchNamesTheColour()
    {
        var world = new World(French) { IgnoreWrites = true };
        var error = await Assert.ThrowsAsync<PpdsException>(() => world.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            OptionSetName = World.Choice, Value = 1, Color = "#112233", SolutionUniqueName = World.Solution
        }));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        Assert.Contains("color: requested '#112233'", error.Message);
    }

    [Theory]
    [InlineData("choice-display")]
    [InlineData("choice-description")]
    [InlineData("table-description")]
    [InlineData("column-description")]
    [InlineData("option-description")]
    [Trait("AC", "AC-142")]
    public async Task Clearing_SendsAnEmptyBaseLanguageLabelOnly_AndIsConfirmedByAnEmptyStoredText(string target)
    {
        var world = new World(French, translation: English);
        switch (target)
        {
            case "choice-display": await world.Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = World.Solution, Name = World.Choice, DisplayName = "" }); break;
            case "choice-description": await world.Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = World.Solution, Name = World.Choice, Description = "" }); break;
            case "table-description": await world.Service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = World.Solution, EntityLogicalName = World.Table, Description = "" }); break;
            case "column-description": await world.Service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = World.Solution, EntityLogicalName = World.Table, ColumnLogicalName = World.NameColumn, Description = "" }); break;
            default: await world.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = World.Choice, Value = 1, Description = "", SolutionUniqueName = World.Solution }); break;
        }
        // A choice update re-sends the stored choice (every other label unchanged, AC-142); only the cleared label is new.
        var written = target.StartsWith("choice", StringComparison.Ordinal)
            ? [target == "choice-display" ? Assert.Single(world.Writes.OfType<UpdateOptionSetRequest>()).OptionSet.DisplayName : Assert.Single(world.Writes.OfType<UpdateOptionSetRequest>()).OptionSet.Description]
            : world.Writes.SelectMany(LabelsIn).Where(l => l.LocalizedLabels.Count > 0).ToList();
        var cleared = Assert.Single(written);
        Assert.Equal(new[] { (French, "") }, cleared.LocalizedLabels.Select(l => (l.LanguageCode, l.Label)));
        Assert.All(world.Writes, w => Assert.True(w is not UpdateEntityRequest and not UpdateAttributeRequest and not UpdateOptionSetRequest and not SdkUpdateOption
            || (bool)w["MergeLabels"], "MergeLabels keeps the other languages' text"));
        Assert.Single(world.ReadBacks);
    }

    // ───── notices: base-language rule (AC-145) ─────

    [Theory]
    [MemberData(nameof(NoticePaths))]
    [Trait("AC", "AC-145")]
    public async Task NonBaseShownLabel_ThatIsStored_GivesTheNotice_NamingBothLanguages(string path)
    {
        // A French-base organisation with English provisioned; the caller's UI language is English and an English
        // translation is stored, so the caller still sees it after a base-language write.
        var world = new World(French, translation: English, callerLanguage: English);
        var result = await world.RunNoticeAsync(path, "Nouveau");
        var notice = Assert.Single(result.ValidationMessages);
        Assert.Equal(MetadataLabelWriteResult.BaseLanguageLabelOnlyRule, notice.Rule);
        Assert.Equal("BASE_LANGUAGE_LABEL_ONLY", notice.Rule);
        Assert.Equal(World.NoticeField(path), notice.Field);
        Assert.Equal($"Only the base-language (LCID {French}) {World.NoticeNoun(path)} is written. The {World.NoticeNoun(path)} Dataverse shows you " +
            $"(LCID {English}) is not changed and still reads '{World.EnglishText(path)}'.", notice.Message);
    }

    [Theory]
    [MemberData(nameof(NoticePaths))]
    [Trait("AC", "AC-145")]
    public async Task NonBaseShownLabel_ThatIsOnlyAFallback_GivesNoNotice(string path)
    {
        // The caller's UI language is English, but no English label is stored: Dataverse shows the base text under the
        // caller's LCID (observed live for an English-only option shown to a French caller, reported with the base LCID),
        // and after the write the caller sees the new text, so there is nothing to warn about.
        var world = new World(French, translation: null, callerLanguage: English);
        Assert.Empty((await world.RunNoticeAsync(path, "Nouveau")).ValidationMessages);
    }

    [Theory]
    [MemberData(nameof(NoticePaths))]
    [Trait("AC", "AC-145")]
    public async Task BaseShownLabel_OrAStoredTranslationThatAlreadyReadsTheText_GivesNoNotice(string path)
    {
        Assert.Empty((await new World(French, translation: English, callerLanguage: French).RunNoticeAsync(path, "Nouveau")).ValidationMessages);
        Assert.Empty((await new World(French, translation: English, callerLanguage: English).RunNoticeAsync(path, World.EnglishText(path))).ValidationMessages);
    }

    [Theory]
    [MemberData(nameof(NoticePaths))]
    [Trait("AC", "AC-145")]
    public async Task OnAFrenchBaseWithoutTranslations_NoFalseNotice(string path)
    {
        // D2 (French Test, 2026-11-17): the English write replaced the French base label while the notice said it was
        // "not changed". The base-language write changes exactly the label the caller is shown, so there is no notice.
        var world = new World(French);
        Assert.Empty((await world.RunNoticeAsync(path, "Nouveau")).ValidationMessages);
    }

    public static TheoryData<string> NoticePaths => new()
    {
        "update-table", "update-column", "update-choice", "update-option", "update-statusreason", "update-column-option"
    };

    [Theory]
    [InlineData("update-option")]
    [InlineData("update-column-option")]
    [Trait("AC", "AC-145")]
    public async Task OptionDryRun_ReadsTheTarget_ReturnsTheSameNotices_AndWritesNothing(string path)
    {
        var world = new World(French, translation: English, callerLanguage: English) { DryRun = true };
        var result = await world.RunNoticeAsync(path, "Nouveau");
        Assert.Single(result.ValidationMessages);
        Assert.Empty(world.Writes);
        Assert.Empty(world.ReadBacks);
    }

    [Theory]
    [InlineData("update-table")]
    [InlineData("update-column")]
    [InlineData("update-choice")]
    [Trait("AC", "AC-145")]
    public async Task InputOnlyDryRun_ReadsNothing_AndGivesNoNotice(string path)
    {
        // Table, column and choice update dry-runs are input-only (MetadataDryRunScope.Inputs; AC-143 for the choice
        // name): computing a notice would need a read the preview says was not made.
        var world = new World(French, translation: English, callerLanguage: English) { DryRun = true };
        Assert.Empty((await world.RunNoticeAsync(path, "Nouveau")).ValidationMessages);
        Assert.Empty(world.All);
    }

    [Fact]
    [Trait("AC", "AC-148")]
    public async Task AnEmptyDescriptionOnCreate_IsNotSentAndNotConfirmed()
    {
        var world = new World(French);
        await world.Service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = World.Solution, SchemaName = "new_created", DisplayName = "Créé", Description = "",
            Options = [new OptionDefinition { Label = "Un", Value = 100000000 }]
        });
        var create = Assert.Single(world.Writes.OfType<CreateOptionSetRequest>());
        Assert.Null(create.OptionSet.Description);
    }

    /// <summary>Every Label reachable from a request's parameters (metadata objects, options, nested option sets).</summary>
    private static IEnumerable<Label> LabelsIn(OrganizationRequest request)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<Label>();
        void Walk(object? value)
        {
            if (value is null || value is string || value.GetType().IsValueType || !seen.Add(value)) return;
            if (value is Label label) { found.Add(label); return; }
            if (value is IEnumerable sequence) { foreach (var item in sequence) Walk(item); return; }
            if (value is not MetadataBase) return;
            foreach (var property in value.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                object? child;
                try { child = property.GetValue(value); } catch { continue; }
                Walk(child);
            }
        }
        foreach (var parameter in request.Parameters.Values) Walk(parameter);
        return found;
    }

    /// <summary>
    /// An offline organisation: a table <c>new_table</c> (primary name <c>new_name</c>, local choice <c>new_pick</c>,
    /// <c>statuscode</c>, <c>statecode</c>) and a global choice <c>new_choice</c>, with labels in the base language and,
    /// optionally, a stored translation; the caller's UI language decides each user-localised label. Writes are
    /// recorded by <see cref="LabelWriteStore"/> and overlaid on the read-backs (unless <see cref="IgnoreWrites"/>).
    /// </summary>
    private sealed class World
    {
        internal const string Solution = "TestSolution";
        internal const string Table = "new_table";
        internal const string NameColumn = "new_name";
        internal const string Pick = "new_pick";
        internal const string Choice = "new_choice";

        private readonly int _base;
        private readonly int? _translation;
        private readonly int _caller;
        private readonly LabelWriteStore _store = new();

        public World(int baseLanguage, int? translation = null, int? callerLanguage = null)
        {
            _base = baseLanguage;
            _translation = translation;
            _caller = callerLanguage ?? baseLanguage;
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() => NewClient());
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard(),
                baseLanguage: new FixedOrganizationBaseLanguage(baseLanguage));
        }

        public DataverseMetadataAuthoringService Service { get; }
        public bool IgnoreWrites { get => _store.IgnoreWrites; init => _store.IgnoreWrites = value; }
        public bool Publish { get; init; }
        public bool DryRun { get; init; }
        public Exception? ReadBackFailure { get; init; }

        /// <summary>Every request except the read-backs, in order.</summary>
        public List<OrganizationRequest> Requests { get; } = [];
        public List<OrganizationRequest> ReadBacks { get; } = [];
        public List<OrganizationRequest> All { get; } = [];
        public IEnumerable<OrganizationRequest> Writes => Requests.Where(r => r is not RetrieveEntityRequest and not RetrieveAttributeRequest
            and not RetrieveOptionSetRequest and not RetrieveAllOptionSetsRequest and not RetrieveEntityKeyRequest and not PublishXmlRequest);

        private IPooledClient NewClient()
        {
            var client = new Mock<IPooledClient>(MockBehavior.Strict);
            client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<QueryBase, CancellationToken>((query, _) =>
                {
                    var name = ((QueryExpression)query).EntityName;
                    var row = new Entity(name);
                    if (name == "solution") row["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111"));
                    else { row["customizationprefix"] = "new"; row["customizationoptionvalueprefix"] = 10000; }
                    return Task.FromResult(new EntityCollection(new List<Entity> { row }));
                });
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) => Task.FromResult(Answer(request)));
            return client.Object;
        }

        private OrganizationResponse Answer(OrganizationRequest request)
        {
            All.Add(request);
            var readBack = _store.FollowsRecordedWrite(request);
            if (readBack)
            {
                ReadBacks.Add(request);
                if (ReadBackFailure != null) throw ReadBackFailure;
            }
            else
            {
                Requests.Add(request);
                _store.Record(request);
            }

            OrganizationResponse response = request switch
            {
                RetrieveEntityRequest r => new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = Entity(r.LogicalName) } },
                RetrieveAttributeRequest r => new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = Attribute(r.LogicalName) } },
                RetrieveOptionSetRequest r => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = GlobalChoice(r.Name) } },
                RetrieveAllOptionSetsRequest => new RetrieveAllOptionSetsResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = new OptionSetMetadataBase[] { GlobalChoice(Choice) } } },
                RetrieveEntityKeyRequest r => new RetrieveEntityKeyResponse { Results = new ParameterCollection { ["EntityKeyMetadata"] = new EntityKeyMetadata { LogicalName = r.LogicalName } } },
                CreateEntityRequest => new CreateEntityResponse { Results = new ParameterCollection { ["EntityId"] = Guid.NewGuid(), ["AttributeId"] = Guid.NewGuid() } },
                UpdateEntityRequest => new UpdateEntityResponse(),
                CreateAttributeRequest => new CreateAttributeResponse { Results = new ParameterCollection { ["AttributeId"] = Guid.NewGuid() } },
                UpdateAttributeRequest => new UpdateAttributeResponse(),
                SdkCreateOneToMany => new CreateOneToManyResponse { Results = new ParameterCollection { ["RelationshipId"] = Guid.NewGuid(), ["AttributeId"] = Guid.NewGuid() } },
                CreateOptionSetRequest => new CreateOptionSetResponse { Results = new ParameterCollection { ["OptionSetId"] = Guid.NewGuid() } },
                UpdateOptionSetRequest => new UpdateOptionSetResponse(),
                SdkUpdateOption => new UpdateOptionValueResponse(),
                SdkUpdateState => new UpdateStateValueResponse(),
                InsertOptionValueRequest i => new InsertOptionValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = i.Value!.Value } },
                InsertStatusValueRequest i => new InsertStatusValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = i.Value!.Value } },
                CreateEntityKeyRequest => new CreateEntityKeyResponse { Results = new ParameterCollection { ["EntityKeyId"] = Guid.NewGuid() } },
                PublishXmlRequest => new PublishXmlResponse(),
                _ => throw new InvalidOperationException("Unexpected SDK request in offline organisation: " + request.RequestName)
            };
            return LabelWriteStore.IsReadBack(request) ? _store.Apply(request, response) : response;
        }

        /// <summary>A stored label: base text, an optional stored translation, and what the caller is shown.</summary>
        private Label L(string baseText, string translatedText)
        {
            var label = new Label();
            label.LocalizedLabels.Add(new LocalizedLabel(baseText, _base));
            if (_translation is { } t) label.LocalizedLabels.Add(new LocalizedLabel(translatedText, t));
            // Dataverse shows the caller's language when stored, otherwise the base text under the caller's LCID
            // (observed live 2026-11-17 for an English-only option shown to a French caller).
            label.UserLocalizedLabel = label.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == _caller) is { } own
                ? new LocalizedLabel(own.Label, own.LanguageCode)
                : new LocalizedLabel(baseText, _caller);
            return label;
        }

        private EntityMetadata Entity(string logicalName)
        {
            var entity = new EntityMetadata { LogicalName = logicalName, SchemaName = logicalName };
            if (logicalName == Table)
            {
                entity.DisplayName = L("Tableau", "Table");
                entity.DisplayCollectionName = L("Tableaux", "Tables");
                entity.Description = L("Description du tableau", "Table description");
                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(entity, "new_tableid");
                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryNameAttribute))!.SetValue(entity, NameColumn);
            }
            return entity;
        }

        private AttributeMetadata Attribute(string logicalName)
        {
            AttributeMetadata attribute = logicalName switch
            {
                NameColumn => new StringAttributeMetadata { MaxLength = 100 },
                Pick => new PicklistAttributeMetadata { OptionSet = Options(global: false, status: false, state: false) },
                "statuscode" => new StatusAttributeMetadata { OptionSet = Options(global: false, status: true, state: false) },
                "statecode" => new StateAttributeMetadata { OptionSet = Options(global: false, status: false, state: true) },
                _ => new StringAttributeMetadata()
            };
            attribute.LogicalName = logicalName;
            attribute.SchemaName = logicalName;
            typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.EntityLogicalName))!.SetValue(attribute, Table);
            attribute.MetadataId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
            if (logicalName is NameColumn or Pick or "statuscode" or "statecode")
            {
                attribute.DisplayName = logicalName == NameColumn ? L("Nom", "Name") : L("Choix", "Pick");
                attribute.Description = logicalName == NameColumn ? L("Description du nom", "Name description") : L("Description du choix", "Pick description");
            }
            if (logicalName is "statuscode" or "statecode")
            {
                typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(attribute,
                    logicalName == "statuscode" ? AttributeTypeCode.Status : AttributeTypeCode.State);
            }
            return attribute;
        }

        private OptionSetMetadata Options(bool global, bool status, bool state)
        {
            var set = new OptionSetMetadata { IsGlobal = global, OptionSetType = OptionSetType.Picklist };
            OptionMetadata Option(int value) => state ? new StateOptionMetadata { Value = value } : status ? new StatusOptionMetadata { Value = value, State = 0 } : new OptionMetadata { Value = value };
            var open = Option(state ? 0 : 1);
            open.Label = L("Ouvert", "Open");
            open.Description = L("Description d'option", "Option description");
            set.Options.Add(open);
            var other = Option(state ? 1 : 2);
            other.Label = L("Autre", "Other");
            set.Options.Add(other);
            return set;
        }

        private OptionSetMetadata GlobalChoice(string name)
        {
            if (name != Choice) return new OptionSetMetadata { Name = name, IsGlobal = true };
            var set = Options(global: true, status: false, state: false);
            set.Name = Choice;
            set.DisplayName = L("Choix global", "Global choice");
            set.Description = L("Description du choix global", "Global choice description");
            return set;
        }

        /// <summary>The texts each path writes (all must appear in the write requests, in the base language).</summary>
        internal static string[] Texts(string path) => path switch
        {
            "create-table" => ["Créé", "Créés", "Décrit", "Nom créé"],
            "update-table" => ["Renommé", "Renommés", "Redécrit"],
            "create-column" => ["Colonne", "Décrite"],
            "create-choice-column" => ["Colonne choix", "Un", "Deux"],
            "create-boolean-column" => ["Booléen", "Oui", "Non"],
            "update-column" => ["Nom renommé", "Nom redécrit"],
            "update-finite-column" => ["Statut renommé"],
            "create-one-to-many" => ["Parent"],
            "create-choice" => ["Créé", "Décrit", "Un", "Deux"],
            "update-choice" => ["Renommé", "Redécrit"],
            "add-option" => ["Ajouté", "Ajouté décrit"],
            "update-option" => ["Fermé", "Fermé décrit"],
            "add-statusreason" => ["Nouveau statut"],
            "update-statusreason" => ["Statut renommé"],
            "add-column-option" => ["Ajouté"],
            "update-column-option" => ["Fermé"],
            "update-state-value" => ["Actif renommé"],
            "create-key" => ["Clé"],
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

        public Task RunAsync(string path) => path switch
        {
            "create-table" => Service.CreateTableAsync(new CreateTableRequest
            {
                SolutionUniqueName = Solution, SchemaName = "new_created", DisplayName = "Créé", PluralDisplayName = "Créés", Description = "Décrit",
                PrimaryAttributeDisplayName = "Nom créé", Publish = Publish
            }),
            "update-table" => Service.UpdateTableAsync(new UpdateTableRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, DisplayName = "Renommé", PluralDisplayName = "Renommés", Description = "Redécrit", Publish = Publish
            }),
            "create-column" => Service.CreateColumnAsync(new CreateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, SchemaName = "new_text", DisplayName = "Colonne", Description = "Décrite",
                ColumnType = SchemaColumnType.String, MaxLength = 100, Publish = Publish
            }),
            "create-choice-column" => Service.CreateColumnAsync(new CreateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, SchemaName = "new_choicecolumn", DisplayName = "Colonne choix",
                ColumnType = SchemaColumnType.Choice, Options = [new OptionDefinition { Label = "Un", Value = 100000000 }, new OptionDefinition { Label = "Deux", Value = 100000001 }]
            }),
            "create-boolean-column" => Service.CreateColumnAsync(new CreateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, SchemaName = "new_flag", DisplayName = "Booléen",
                ColumnType = SchemaColumnType.Boolean, TrueLabel = "Oui", FalseLabel = "Non"
            }),
            "update-column" => Service.UpdateColumnAsync(new UpdateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = NameColumn, DisplayName = "Nom renommé", Description = "Nom redécrit", Publish = Publish
            }),
            "update-finite-column" => Service.UpdateColumnAsync(new UpdateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = "statuscode", DisplayName = "Statut renommé", Publish = Publish
            }),
            "create-one-to-many" => Service.CreateOneToManyAsync(new PPDS.Dataverse.Metadata.Authoring.CreateOneToManyRequest
            {
                SolutionUniqueName = Solution, ReferencedEntity = Table, ReferencingEntity = "new_child", SchemaName = "new_table_child", LookupSchemaName = "new_parent", LookupDisplayName = "Parent"
            }),
            "create-choice" => Service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
            {
                SolutionUniqueName = Solution, SchemaName = "new_created", DisplayName = "Créé", Description = "Décrit",
                Options = [new OptionDefinition { Label = "Un", Value = 100000000 }, new OptionDefinition { Label = "Deux", Value = 100000001 }]
            }),
            "update-choice" => Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = Choice, DisplayName = "Renommé", Description = "Redécrit" }),
            "add-option" => Service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = Choice, Label = "Ajouté", Description = "Ajouté décrit", Value = 100000005, SolutionUniqueName = Solution }),
            "update-option" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, NewLabel = "Fermé", Description = "Fermé décrit", SolutionUniqueName = Solution }),
            "add-statusreason" => Service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = Table, Label = "Nouveau statut", StateCode = 0, Value = 7, SolutionUniqueName = Solution, Publish = Publish }),
            "update-statusreason" => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = Table, Value = 1, NewLabel = "Statut renommé", SolutionUniqueName = Solution, Publish = Publish }),
            "add-column-option" => Service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Pick, Label = "Ajouté", Value = 7, SolutionUniqueName = Solution, Publish = Publish }),
            "update-column-option" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Pick, Value = 1, NewLabel = "Fermé", SolutionUniqueName = Solution, Publish = Publish }),
            "update-state-value" => Service.UpdateStateValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest { EntityLogicalName = Table, AttributeLogicalName = "statecode", Value = 0, Label = "Actif renommé" }),
            "create-key" => Service.CreateKeyAsync(new CreateKeyRequest { SolutionUniqueName = Solution, EntityLogicalName = Table, SchemaName = "new_key", DisplayName = "Clé", KeyAttributes = [NameColumn] }),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

        internal static string NoticeField(string path) => path is "update-table" or "update-column" or "update-choice" ? "DisplayName" : "NewLabel";
        internal static string NoticeNoun(string path) => path is "update-table" or "update-column" or "update-choice" ? "display name" : "label";
        internal static string EnglishText(string path) => path switch
        {
            "update-table" => "Table",
            "update-column" => "Name",
            "update-choice" => "Global choice",
            _ => "Open"
        };

        /// <summary>Updates one label (the one the notice is about) to <paramref name="text"/>.</summary>
        public Task<MetadataLabelWriteResult> RunNoticeAsync(string path, string text) => path switch
        {
            "update-table" => Service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = Table, DisplayName = text, DryRun = DryRun }),
            "update-column" => Service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = NameColumn, DisplayName = text, DryRun = DryRun }),
            "update-choice" => Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = Choice, DisplayName = text, DryRun = DryRun }),
            "update-option" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, NewLabel = text, SolutionUniqueName = Solution, DryRun = DryRun }),
            "update-statusreason" => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = Table, Value = 1, NewLabel = text, SolutionUniqueName = Solution }),
            "update-column-option" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Pick, Value = 1, NewLabel = text, SolutionUniqueName = Solution, DryRun = DryRun }),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
    }
}
