using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkUpdateState = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Offline model of what Dataverse stores after a label or colour write, for fakes whose canned metadata would otherwise
/// make the service's read-back (as-if-published) report WRITE_NOT_CONFIRMED. <see cref="Record"/> remembers every label,
/// description and colour a write request carries (merged by LCID, as MergeLabels does); <see cref="Apply"/> overlays them
/// on the response to a later as-if-published read of the same target, and <see cref="Synthesize"/> builds a minimal
/// response for such a read when the fake has none. Published reads are left as the fake returns them.
/// </summary>
internal sealed class LabelWriteStore
{
    private readonly Dictionary<string, Dictionary<string, Label>> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, writes are recorded but not stored (models Dataverse acknowledging a write it ignored).</summary>
    public bool IgnoreWrites { get; set; }

    /// <summary>
    /// When set, the option labels a create carries (local options, Boolean true/false labels, a new global choice's
    /// options) are not stored, while the column or choice's own labels are: models a create that kept only part of
    /// what it was sent.
    /// </summary>
    public bool IgnoreCreatedOptionLabels { get; set; }

    private readonly HashSet<string> _booleans = new(StringComparer.OrdinalIgnoreCase);

    private Label? OptionLabel(Label? label) => IgnoreCreatedOptionLabels ? null : label;

    /// <summary>False leaves option colours as the fixture returns them (fixtures that model stored colours themselves).</summary>
    public bool OverlayColors { get; set; } = true;

    private Moq.Mock<PPDS.Dataverse.Pooling.IPooledClient>? _attached;
    private int _replayed;

    /// <summary>
    /// Answers every as-if-published read-back sent through <paramref name="client"/> from the writes previously sent
    /// through it (replayed from the mock's invocations), so the fixture's own handlers never see the read-back. Call it
    /// after the fixture's catch-all ExecuteAsync setup.
    /// </summary>
    public static LabelWriteStore Attach(Moq.Mock<PPDS.Dataverse.Pooling.IPooledClient> client)
    {
        var store = new LabelWriteStore { _attached = client };
        Moq.Language.Flow.ISetup<PPDS.Dataverse.Pooling.IPooledClient, Task<OrganizationResponse>> setup =
            client.Setup(c => c.ExecuteAsync(Moq.It.Is<OrganizationRequest>(r => store.FollowsAWrite(r)), Moq.It.IsAny<CancellationToken>()));
        setup.Returns<OrganizationRequest, CancellationToken>((request, _) =>
        {
            store.Replay();
            return Task.FromResult(store.Synthesize(request));
        });
        return store;
    }

    /// <summary>
    /// True for an as-if-published read that directly follows a write (possibly after other such reads): the
    /// service's read-back. A read before any write, or after an unrelated read, is left to the fixture.
    /// </summary>
    private bool FollowsAWrite(OrganizationRequest request)
    {
        if (!IsReadBack(request)) return false;
        var previous = _attached!.Invocations.Where(i => i.Method.Name == "ExecuteAsync")
            .Select(i => (OrganizationRequest)i.Arguments[0]).Where(r => !ReferenceEquals(r, request)).ToList();
        for (var i = previous.Count - 1; i >= 0; i--)
        {
            if (IsWrite(previous[i])) return true;
            if (!IsReadBack(previous[i])) return false;
        }
        return false;
    }

    private static bool IsWrite(OrganizationRequest request) => request is UpdateEntityRequest or CreateEntityRequest or UpdateAttributeRequest
        or CreateAttributeRequest or CreateOneToManyRequest or CreateEntityKeyRequest or UpdateOptionSetRequest or CreateOptionSetRequest
        or SdkUpdateOption or InsertOptionValueRequest or InsertStatusValueRequest or SdkUpdateState;

    private void Replay()
    {
        var calls = _attached!.Invocations.Where(i => i.Method.Name == "ExecuteAsync").ToList();
        for (; _replayed < calls.Count; _replayed++)
        {
            var request = (OrganizationRequest)calls[_replayed].Arguments[0];
            if (IsReadBack(request)) continue;
            Record(request);
            if (calls[_replayed].ReturnValue is Task<OrganizationResponse> { IsCompletedSuccessfully: true } done)
            {
                if (done.Result is InsertOptionValueResponse inserted) InsertedValue(inserted.NewOptionValue);
                if (done.Result is CreateOptionSetResponse { Results: var results } && request is CreateOptionSetRequest create
                    && results.TryGetValue("OptionSetId", out var id) && id is Guid choiceId)
                    ChoiceCreated(choiceId, create.OptionSet.Name);
            }
        }
    }

    public void Record(OrganizationRequest request)
    {
        if (IgnoreWrites) return;
        // An update sent without MergeLabels = true replaces the whole label: Dataverse drops the languages the
        // request does not carry. Creates and inserts have nothing to merge with.
        _replacing = request is UpdateEntityRequest or UpdateAttributeRequest or UpdateOptionSetRequest or SdkUpdateOption or SdkUpdateState
            && !(request.Parameters.TryGetValue("MergeLabels", out var merge) && merge is true);
        switch (request)
        {
            case UpdateEntityRequest u:
                Entity(u.Entity.LogicalName, u.Entity); break;
            case CreateEntityRequest c:
                Entity(c.Entity.SchemaName, c.Entity);
                Put(AttributeKey(c.Entity.SchemaName, c.PrimaryAttribute.SchemaName), "display", c.PrimaryAttribute.DisplayName);
                break;
            case UpdateAttributeRequest u:
                Attribute(u.EntityName, u.Attribute.LogicalName ?? u.Attribute.SchemaName, u.Attribute); break;
            case CreateAttributeRequest c:
                Attribute(c.EntityName, c.Attribute.SchemaName, c.Attribute);
                if (c.Attribute is EnumAttributeMetadata { OptionSet: { IsGlobal: not true } local })
                    foreach (var o in local.Options.Where(o => o.Value.HasValue))
                        Option(AttributeKey(c.EntityName, c.Attribute.SchemaName), o.Value!.Value, OptionLabel(o.Label), o.Description, o.Color);
                if (c.Attribute is BooleanAttributeMetadata { OptionSet: { } flags })
                {
                    _booleans.Add(AttributeKey(c.EntityName, c.Attribute.SchemaName));
                    foreach (var o in new[] { flags.TrueOption, flags.FalseOption }.Where(o => o?.Value is not null))
                        Option(AttributeKey(c.EntityName, c.Attribute.SchemaName), o!.Value!.Value, OptionLabel(o.Label), null, null);
                }
                break;
            case CreateOneToManyRequest c:
                Put(AttributeKey(c.OneToManyRelationship.ReferencingEntity, c.Lookup.SchemaName), "display", c.Lookup.DisplayName); break;
            case CreateEntityKeyRequest c:
                Put($"key|{c.EntityName}|{c.EntityKey.SchemaName}", "display", c.EntityKey.DisplayName); break;
            case UpdateOptionSetRequest u:
                Choice(u.OptionSet.Name, u.OptionSet); break;
            case CreateOptionSetRequest c:
                Choice(c.OptionSet.Name, c.OptionSet);
                if (c.OptionSet is OptionSetMetadata set)
                    foreach (var o in set.Options.Where(o => o.Value.HasValue))
                        Option(ChoiceKey(c.OptionSet.Name), o.Value!.Value, OptionLabel(o.Label), o.Description, o.Color);
                break;
            case SdkUpdateOption u:
                Option(OptionTarget(u, P<string>(u, "OptionSetName")), P<int>(u, "Value"), P<Label>(u, "Label"), P<Label>(u, "Description"), Color(u)); break;
            case InsertOptionValueRequest i:
                if (P<int?>(i, "Value") is { } inserted) Option(OptionTarget(i, P<string>(i, "OptionSetName")), inserted, P<Label>(i, "Label"), P<Label>(i, "Description"), Color(i));
                else PendingInsert = (OptionTarget(i, P<string>(i, "OptionSetName")), P<Label>(i, "Label"), P<Label>(i, "Description"), Color(i));
                break;
            case InsertStatusValueRequest i:
                if (P<int?>(i, "Value") is { } status) Option(AttributeKey(P<string>(i, "EntityLogicalName") ?? "", "statuscode"), status, P<Label>(i, "Label"), P<Label>(i, "Description"), Color(i));
                break;
            case SdkUpdateState u:
                Option(AttributeKey(P<string>(u, "EntityLogicalName") ?? "", P<string>(u, "AttributeLogicalName") ?? ""), P<int>(u, "Value"), P<Label>(u, "Label"), null, null); break;
        }
    }

    private int _readBacksDue;

    /// <summary>
    /// For manually fed stores: true for an as-if-published read that is the service's read-back of the write just
    /// sent (two reads after a table create: the table and its primary column; one after any other write). Call before
    /// <see cref="Record"/>.
    /// </summary>
    public bool FollowsRecordedWrite(OrganizationRequest request)
    {
        if (IsReadBack(request) && _readBacksDue > 0) { _readBacksDue--; return true; }
        _readBacksDue = request is CreateEntityRequest ? 2 : IsWrite(request) ? 1 : 0;
        return false;
    }

    /// <summary>An insert without a value: completed by <see cref="InsertedValue"/> once the fake assigns one.</summary>
    private (string Target, Label? Label, Label? Description, string? Color)? PendingInsert { get; set; }

    /// <summary>Records the value a fake assigned to the last insert sent without one.</summary>
    public void InsertedValue(int value)
    {
        if (PendingInsert is { } p) Option(p.Target, value, p.Label, p.Description, p.Color);
        PendingInsert = null;
    }

    private readonly Dictionary<Guid, string> _choiceIds = new();

    /// <summary>Records the id the fake assigned to a created global choice, so a read-back by that id finds its labels.</summary>
    public void ChoiceCreated(Guid id, string name) => _choiceIds[id] = name;

    /// <summary>The global choice a RetrieveOptionSet names: by name, or by the id of a choice created through this store.</summary>
    private string ChoiceName(RetrieveOptionSetRequest r)
        => !string.IsNullOrEmpty(r.Name) ? r.Name : _choiceIds.TryGetValue(r.MetadataId, out var name) ? name : "";

    /// <summary>True for a read-back request (as-if-published read of a single target).</summary>
    public static bool IsReadBack(OrganizationRequest request) => request switch
    {
        RetrieveEntityRequest r => r.RetrieveAsIfPublished,
        RetrieveAttributeRequest r => r.RetrieveAsIfPublished,
        RetrieveOptionSetRequest r => r.RetrieveAsIfPublished,
        RetrieveEntityKeyRequest r => r.RetrieveAsIfPublished,
        _ => false
    };

    /// <summary>Overlays recorded writes on the response to an as-if-published read; returns the response.</summary>
    public OrganizationResponse Apply(OrganizationRequest request, OrganizationResponse response)
    {
        switch (request)
        {
            case RetrieveEntityRequest { RetrieveAsIfPublished: true } r when response.Results.TryGetValue("EntityMetadata", out var e) && e is EntityMetadata entity:
                ApplyEntity(r.LogicalName, entity); break;
            case RetrieveAttributeRequest { RetrieveAsIfPublished: true } r when response.Results.TryGetValue("AttributeMetadata", out var a) && a is AttributeMetadata attribute:
                ApplyAttribute(r.EntityLogicalName, r.LogicalName, attribute); break;
            case RetrieveOptionSetRequest { RetrieveAsIfPublished: true } r when response.Results.TryGetValue("OptionSetMetadata", out var o) && o is OptionSetMetadataBase set:
                ApplyChoice(ChoiceName(r), set); break;
            case RetrieveEntityKeyRequest { RetrieveAsIfPublished: true } r when response.Results.TryGetValue("EntityKeyMetadata", out var k) && k is EntityKeyMetadata key:
                if (Get($"key|{r.EntityLogicalName}|{r.LogicalName}", "display") is { } display) key.DisplayName = display;
                break;
        }
        return response;
    }

    /// <summary>A minimal response for a read-back the fake does not model, built from the recorded writes.</summary>
    public OrganizationResponse Synthesize(OrganizationRequest request)
    {
        OrganizationResponse response = request switch
        {
            RetrieveEntityRequest r => new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = new EntityMetadata { LogicalName = r.LogicalName } } },
            RetrieveAttributeRequest r => new RetrieveAttributeResponse
            {
                Results = new ParameterCollection
                {
                    ["AttributeMetadata"] = _booleans.Contains(AttributeKey(r.EntityLogicalName, r.LogicalName))
                        ? new BooleanAttributeMetadata { LogicalName = r.LogicalName, OptionSet = new BooleanOptionSetMetadata(new OptionMetadata { Value = 1 }, new OptionMetadata { Value = 0 }) }
                        : _options.Keys.Any(k => k.StartsWith(AttributeKey(r.EntityLogicalName, r.LogicalName) + "|", StringComparison.OrdinalIgnoreCase))
                        ? new PicklistAttributeMetadata { LogicalName = r.LogicalName, OptionSet = new OptionSetMetadata() }
                        : new AttributeMetadata { LogicalName = r.LogicalName }
                }
            },
            RetrieveOptionSetRequest r => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = new OptionSetMetadata { Name = ChoiceName(r) } } },
            RetrieveEntityKeyRequest r => new RetrieveEntityKeyResponse { Results = new ParameterCollection { ["EntityKeyMetadata"] = new EntityKeyMetadata { LogicalName = r.LogicalName } } },
            _ => throw new InvalidOperationException("Not a read-back request: " + request.RequestName)
        };
        return Apply(request, response);
    }

    // ───── storage ─────

    private readonly Dictionary<string, (Label? Label, Label? Description, string? Color)> _options = new(StringComparer.OrdinalIgnoreCase);

    private static string EntityKey(string entity) => $"entity|{entity}";
    private static string AttributeKey(string entity, string attribute) => $"attr|{entity}|{attribute}";
    private static string ChoiceKey(string name) => $"choice|{name}";

    private static string OptionTarget(OrganizationRequest request, string? optionSetName)
        => request.Parameters.TryGetValue("EntityLogicalName", out var e) && e is string entity && !string.IsNullOrEmpty(entity)
            && request.Parameters.TryGetValue("AttributeLogicalName", out var a) && a is string attribute && !string.IsNullOrEmpty(attribute)
            ? AttributeKey(entity, attribute) : ChoiceKey(optionSetName ?? "");

    /// <summary>A request parameter, or default when absent (the SDK property getters throw for an absent parameter).</summary>
    private static T? P<T>(OrganizationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && value is T typed ? typed : default;

    private static string? Color(OrganizationRequest request) => request.Parameters.TryGetValue("Color", out var c) ? c as string : null;

    private void Entity(string name, EntityMetadata e)
    {
        Put(EntityKey(name), "display", e.DisplayName);
        Put(EntityKey(name), "plural", e.DisplayCollectionName);
        Put(EntityKey(name), "description", e.Description);
    }

    private void Attribute(string entity, string attribute, AttributeMetadata a)
    {
        Put(AttributeKey(entity, attribute), "display", a.DisplayName);
        Put(AttributeKey(entity, attribute), "description", a.Description);
    }

    private void Choice(string name, OptionSetMetadataBase s)
    {
        Put(ChoiceKey(name), "display", s.DisplayName);
        Put(ChoiceKey(name), "description", s.Description);
    }

    private void Option(string target, int value, Label? label, Label? description, string? color)
    {
        var key = $"{target}|{value}";
        _options.TryGetValue(key, out var current);
        _options[key] = (Store(current.Label, label), Store(current.Description, description), color ?? current.Color);
    }

    private void Put(string target, string part, Label? label)
    {
        if (label is null) return;
        if (!_labels.TryGetValue(target, out var parts)) _labels[target] = parts = new(StringComparer.OrdinalIgnoreCase);
        parts.TryGetValue(part, out var current);
        parts[part] = Store(current, label)!;
    }

    private bool _replacing;
    private readonly HashSet<Label> _replaced = new(ReferenceEqualityComparer.Instance);

    /// <summary>Merges <paramref name="written"/> into <paramref name="stored"/>, or replaces it for a write without MergeLabels.</summary>
    private Label? Store(Label? stored, Label? written)
    {
        if (written is null || !_replacing) return Merge(stored, written);
        var replaced = Merge(null, written)!;
        _replaced.Add(replaced);
        return replaced;
    }

    private Label? Get(string target, string part) => _labels.TryGetValue(target, out var parts) && parts.TryGetValue(part, out var l) ? l : null;

    private static Label? Merge(Label? stored, Label? written)
    {
        if (written is null) return stored;
        var merged = new Label();
        foreach (var l in stored?.LocalizedLabels ?? []) merged.LocalizedLabels.Add(new LocalizedLabel(l.Label, l.LanguageCode));
        foreach (var l in written.LocalizedLabels)
        {
            var existing = merged.LocalizedLabels.FirstOrDefault(x => x.LanguageCode == l.LanguageCode);
            if (existing != null) merged.LocalizedLabels.Remove(existing);
            merged.LocalizedLabels.Add(new LocalizedLabel(l.Label, l.LanguageCode));
        }
        merged.UserLocalizedLabel = written.UserLocalizedLabel ?? stored?.UserLocalizedLabel;
        return merged;
    }

    private Label? Overlay(Label? read, Label? stored)
    {
        if (stored is null) return read;
        // A label replaced by a write without MergeLabels keeps none of the languages the read had.
        var merged = _replaced.Contains(stored) ? Merge(null, stored)! : Merge(read, stored)!;
        var shown = read?.UserLocalizedLabel;
        merged.UserLocalizedLabel = shown is null ? stored.UserLocalizedLabel
            : merged.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == shown.LanguageCode) ?? shown;
        return merged;
    }

    private void ApplyEntity(string entity, EntityMetadata e)
    {
        e.DisplayName = Overlay(e.DisplayName, Get(EntityKey(entity), "display"));
        e.DisplayCollectionName = Overlay(e.DisplayCollectionName, Get(EntityKey(entity), "plural"));
        e.Description = Overlay(e.Description, Get(EntityKey(entity), "description"));
    }

    private void ApplyAttribute(string entity, string attribute, AttributeMetadata a)
    {
        a.DisplayName = Overlay(a.DisplayName, Get(AttributeKey(entity, attribute), "display"));
        a.Description = Overlay(a.Description, Get(AttributeKey(entity, attribute), "description"));
        if (a is EnumAttributeMetadata { OptionSet: { } set })
            ApplyOptions(AttributeKey(entity, attribute), set);
        if (a is BooleanAttributeMetadata { OptionSet: { } flags })
            foreach (var option in new[] { flags.TrueOption, flags.FalseOption }.Where(o => o?.Value is not null))
                if (_options.TryGetValue($"{AttributeKey(entity, attribute)}|{option!.Value}", out var stored))
                    option.Label = Overlay(option.Label, stored.Label);
    }

    private void ApplyChoice(string name, OptionSetMetadataBase s)
    {
        s.DisplayName = Overlay(s.DisplayName, Get(ChoiceKey(name), "display"));
        s.Description = Overlay(s.Description, Get(ChoiceKey(name), "description"));
        if (s is OptionSetMetadata set)
            ApplyOptions(ChoiceKey(name), set);
    }

    private void ApplyOptions(string target, OptionSetMetadata set)
    {
        foreach (var (key, stored) in _options.Where(o => o.Key.StartsWith(target + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var value = int.Parse(key[(target.Length + 1)..]);
            var option = set.Options.FirstOrDefault(o => o.Value == value);
            if (option is null)
            {
                option = target.EndsWith("|statuscode", StringComparison.OrdinalIgnoreCase) ? new StatusOptionMetadata { Value = value, State = 0 } : new OptionMetadata { Value = value };
                set.Options.Add(option);
            }
            option.Label = Overlay(option.Label, stored.Label);
            option.Description = Overlay(option.Description, stored.Description);
            if (stored.Color != null && OverlayColors) option.Color = stored.Color;
        }
    }
}
