using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Label targeting safety for draft-aware update/remove (#1531). Targets resolve from unpublished (draft)
/// metadata, while list commands show published labels. A label that names different options in the two views
/// is refused, so <c>--label</c> never acts on an option other than the one a listing displays under that label.
/// </summary>
public partial class DataverseMetadataAuthoringService
{
    private async Task EnsureStatusReasonLabelAgreesWithPublishedAsync(
        string entityLogicalName, string label, IEnumerable<int> draftValues, CancellationToken ct)
    {
        var published = await ReadStatusReasonsAsync(entityLogicalName, retrieveAsIfPublished: false, ct).ConfigureAwait(false);
        EnsureLabelAgreesWithPublished(label, draftValues,
            published.Where(r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)).Select(r => r.Value),
            $"status reasons on '{entityLogicalName}'");
    }

    /// <summary>
    /// Global option variant: reads the published option set under its already-resolved stored name (no further
    /// name resolution) and applies the same agreement rule.
    /// </summary>
    private async Task EnsureGlobalOptionLabelAgreesWithPublishedAsync(
        string optionSetName, string label, IEnumerable<int> draftValues, CancellationToken ct)
    {
        OptionSetMetadata? published;
        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            var response = await client.ExecuteAsync(new RetrieveOptionSetRequest { Name = optionSetName }, ct).ConfigureAwait(false);
            published = (response as RetrieveOptionSetResponse)?.OptionSetMetadata as OptionSetMetadata;
        }
        catch (OperationCanceledException)
        {
            throw; // R2: cancellation must propagate
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
        {
            throw new PpdsException(MetadataErrorCodes.EntityNotFound,
                $"Could not retrieve option set '{optionSetName}'.", ex);
        }

        var publishedValues = (published?.Options ?? [])
            .Where(o => o.Value.HasValue && string.Equals(
                o.Label?.UserLocalizedLabel?.Label ?? o.Label?.LocalizedLabels?.FirstOrDefault()?.Label ?? "",
                label, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Value!.Value);
        EnsureLabelAgreesWithPublished(label, draftValues, publishedValues, $"options in option set '{optionSetName}'");
    }

    /// <summary>
    /// A requested label as the single label in the organisation's base language <paramref name="baseLanguage"/> (every
    /// label write also sets MergeLabels, so other languages keep their text); null when the caller did not ask to change
    /// it. Writing any other LCID is unsafe: in an organisation whose base language is 1036 without English provisioned,
    /// a 1033 label was silently ignored by UpdateOptionSet and (in one probe) UpdateOptionValue, rejected (0x80044195) by
    /// InsertOptionValue/InsertStatusValue, the status-reason update and (in a later replay) UpdateOptionValue, and stored
    /// as the 1036 label by UpdateEntity/UpdateAttribute and the create messages (live, 2026-11-17, a French-base
    /// organisation; MetadataGlobalChoiceLiveTests.BaseLanguage_D1/D2/D3 record the replay). Asserted live on an English-base
    /// organisation: an UpdateOptionValue without a Label leaves the label unchanged, so a colour- or description-only
    /// update never rewrites it, and a requested base-language label with MergeLabels keeps the French translation.
    /// </summary>
    private static Label? BaseLanguageLabel(string? requested, int baseLanguage)
        => requested == null ? null : new Label(requested, baseLanguage);

    /// <summary>
    /// Notices for requested labels written in the base language while the label Dataverse shows the caller (its
    /// user-localised label) is a STORED label in another language that the write does not change. A notice is given only
    /// when (1) the shown label's LCID is not the base language, (2) a label in that LCID is actually stored on the target
    /// (it appears in <see cref="Label.LocalizedLabels"/>), and (3) its stored text differs from the requested text. When the
    /// shown label is only a fallback (no stored label in that LCID), the caller is shown the new base-language text after
    /// the write, so there is nothing to warn about. Observed live 2026-11-17: a table description created with English text
    /// only is stored under 1033 AND 1036 once French is provisioned (a real copy the write leaves as it was), while an option
    /// created in English only is stored under 1033 alone. The message names both LCIDs rather than claiming either is "your
    /// language". Surfaces show notices as warnings; without them a caller could see no change in their own listing after
    /// a successful update.
    /// </summary>
    private static MetadataLabelWriteResult BaseLanguageNotices(int baseLanguage, params (string Field, string Noun, string? Requested, Label? Existing)[] labels)
    {
        var notices = new List<ValidationMessage>();
        foreach (var (field, noun, requested, existing) in labels)
        {
            if (requested == null || existing?.UserLocalizedLabel is not { } shown || shown.LanguageCode == baseLanguage)
                continue;
            var stored = existing.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == shown.LanguageCode);
            if (stored is null || string.Equals(stored.Label, requested, StringComparison.Ordinal))
                continue;
            notices.Add(new ValidationMessage
            {
                Field = field,
                Rule = MetadataLabelWriteResult.BaseLanguageLabelOnlyRule,
                Message = $"Only the base-language (LCID {baseLanguage}) {noun} is written. The {noun} Dataverse shows you " +
                    $"(LCID {shown.LanguageCode}) is not changed and still reads '{stored.Label}'."
            });
        }

        return notices.Count == 0 ? MetadataLabelWriteResult.None : new MetadataLabelWriteResult { ValidationMessages = notices };
    }

    /// <summary>Resolves a local column option from draft metadata, applying the label safety check.</summary>
    private async Task<(ColumnOptionInfo Info, Label? Labels)> ResolveDraftColumnOptionAsync(
        string entityLogicalName, string columnLogicalName, int? value, string? label, CancellationToken ct)
    {
        var draftEntries = await ReadColumnOptionEntriesAsync(entityLogicalName, columnLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);
        var drafts = draftEntries.Select(entry => entry.Info).ToList();
        if (!value.HasValue)
        {
            var draftValues = drafts.Where(o => string.Equals(o.Label, label, StringComparison.OrdinalIgnoreCase)).Select(o => o.Value).ToList();
            if (draftValues.Count <= 1) // A duplicate draft label is reported by ResolveColumnOption's existing ambiguity error.
            {
                var published = await ReadColumnOptionsAsync(entityLogicalName, columnLogicalName, retrieveAsIfPublished: false, ct).ConfigureAwait(false);
                EnsureLabelAgreesWithPublished(label!, draftValues,
                    published.Where(o => string.Equals(o.Label, label, StringComparison.OrdinalIgnoreCase)).Select(o => o.Value),
                    $"options on '{entityLogicalName}.{columnLogicalName}'");
            }
        }

        var target = ResolveColumnOption(drafts, value, label, entityLogicalName, columnLogicalName);
        return (target, draftEntries.First(entry => ReferenceEquals(entry.Info, target)).Labels);
    }

    /// <summary>
    /// Refuses label targeting when the published view shows the label on any option other than the single
    /// draft match: a different value, several values, or a label that exists only in the published view.
    /// A label that exists only in drafts (added or relabelled and not yet published) is allowed.
    /// </summary>
    private static void EnsureLabelAgreesWithPublished(string label, IEnumerable<int> draftValues, IEnumerable<int> publishedValues, string scope)
    {
        var draft = draftValues.Distinct().ToList();
        var published = publishedValues.Distinct().ToList();
        if (published.Count == 0 || (draft.Count == 1 && published.Count == 1 && draft[0] == published[0]))
            return;

        throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
            $"Label '{label}' identifies different {scope} in published metadata (values: {string.Join(", ", published)}) " +
            $"and unpublished changes (values: {(draft.Count == 0 ? "none" : string.Join(", ", draft))}). " +
            "Target the option with --value, or publish first.",
            "Label");
    }
}
