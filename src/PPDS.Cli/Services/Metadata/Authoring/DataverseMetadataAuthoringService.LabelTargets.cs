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

    /// <summary>Resolves a local column option from draft metadata, applying the label safety check.</summary>
    private async Task<ColumnOptionInfo> ResolveDraftColumnOptionAsync(
        string entityLogicalName, string columnLogicalName, int? value, string? label, CancellationToken ct)
    {
        var drafts = await ReadColumnOptionsAsync(entityLogicalName, columnLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);
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

        return ResolveColumnOption(drafts, value, label, entityLogicalName, columnLogicalName);
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
