using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Read-back of committed label (and colour) writes. Dataverse can acknowledge a label write without storing it (on an
/// organisation whose base language is 1036, a 1033 label on UpdateOptionSet, and in one probe on UpdateOptionValue, was
/// accepted and not stored; a later replay of UpdateOptionValue was rejected with 0x80044195 instead), so
/// every write that sends a label is followed by one as-if-published read of the target, after the caches are
/// invalidated and before any publish, and the base-language text must equal what was sent (exact, ordinal). An empty
/// requested text is confirmed by an empty or absent base-language label. A mismatch or a failed read raises
/// <see cref="MetadataErrorCodes.WriteNotConfirmed"/>, saying what was written and that publishing was not attempted.
/// Option writes confirm label, description and colour with the same single read.
/// </summary>
public partial class DataverseMetadataAuthoringService
{
    /// <summary>One label to confirm: what it is called, the text that was sent, and where it is on the read target.</summary>
    private readonly record struct LabelExpectation<T>(string Noun, string? Expected, Func<T, Label?> Select);

    /// <summary>
    /// Reads <paramref name="target"/> once and confirms every expectation whose text was sent. Sends nothing when no
    /// label was sent.
    /// </summary>
    private async Task ConfirmLabelsAsync<T>(
        string targetNoun, string written, bool publishRequested, int baseLanguage,
        Func<CancellationToken, Task<T>> target, CancellationToken ct, params LabelExpectation<T>[] labels)
    {
        var sent = labels.Where(l => l.Expected != null).ToArray();
        if (sent.Length == 0)
            return;

        var notPublished = publishRequested ? " Publication (--publish) was not attempted." : "";
        T read;
        try
        {
            // The write is committed: a cancellation from here on is "written, not confirmed", never a bare cancellation.
            ct.ThrowIfCancellationRequested();
            read = await target(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new PpdsException(MetadataErrorCodes.WriteNotConfirmed,
                $"{written} Its {string.Join(" and ", sent.Select(l => l.Noun))} could not be confirmed: {ConfirmationReadFailure(ex)}.{notPublished} " +
                $"Check the {targetNoun} before re-running the command.",
                ReauthenticationContext(ex), ex);
        }

        var mismatches = sent
            .Select(l => (l.Noun, l.Expected!, Stored: BaseText(l.Select(read), baseLanguage)))
            .Where(l => !BaseTextMatches(l.Stored, l.Item2))
            .Select(l => $"{l.Noun}: requested '{l.Item2}' (LCID {baseLanguage}), stored '{l.Stored ?? "none"}'")
            .ToList();
        if (mismatches.Count > 0)
            throw new PpdsException(MetadataErrorCodes.WriteNotConfirmed,
                $"{written} Dataverse did not store its {string.Join("; its ", mismatches)}.{notPublished} Check the {targetNoun} before re-running the command.");
        _logger?.LogInformation("Confirmed {Count} label(s) on {Target}", sent.Length, targetNoun);
    }

    /// <summary>
    /// Confirms an option write (global option, local option, status reason or state value) with one as-if-published
    /// read: the base-language label and description when sent, and the colour when sent. Sends nothing when none was sent.
    /// Every failure from here on is <c>WRITE_NOT_CONFIRMED</c> naming what was committed and, when publication was
    /// requested, that it was not attempted; the original failure is the inner exception. A value Dataverse accepted but did
    /// not store can therefore never be reported as applied (#1234), and a caller can tell "written but not confirmed" from
    /// <c>SDK_OPERATION_FAILED</c> (the request itself failed). When the read failed because the sign-in expired,
    /// <c>Context["requiresReauthentication"]</c> is true. The colour rides on the write as the message's <c>Color</c>
    /// parameter (see <see cref="WithColor"/>; specs/metadata-authoring.md "Option colours").
    /// </summary>
    private async Task ConfirmOptionWriteAsync(
        string? optionSetName, string? entityLogicalName, string? attributeLogicalName, int value, int baseLanguage,
        string? label, string? description, string? color, string written, bool publishRequested, CancellationToken ct)
    {
        var hasColor = !string.IsNullOrEmpty(color);
        if (label == null && description == null && !hasColor)
            return;

        var notPublished = publishRequested ? " Publication (--publish) was not attempted." : "";
        var parts = new List<string>();
        if (label != null) parts.Add("label");
        if (description != null) parts.Add("description");
        if (hasColor) parts.Add($"color '{color}'");

        OptionMetadata stored;
        try
        {
            ct.ThrowIfCancellationRequested(); // committed: a cancellation from here on is "written, not confirmed"
            stored = await ReadStoredOptionAsync(optionSetName, entityLogicalName, attributeLogicalName, value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only fixed, service-authored text is quoted: PPDS errors carry no provider diagnostics in their message.
            throw new PpdsException(MetadataErrorCodes.WriteNotConfirmed,
                $"{written} Its {string.Join(" and ", parts)} could not be confirmed: {ConfirmationReadFailure(ex)}.{notPublished} Check the option before re-running the command.",
                ReauthenticationContext(ex), ex);
        }

        var mismatches = new List<string>();
        if (label != null && BaseText(stored.Label, baseLanguage) is var storedLabel && !BaseTextMatches(storedLabel, label))
            mismatches.Add($"label: requested '{label}' (LCID {baseLanguage}), stored '{storedLabel ?? "none"}'");
        if (description != null && BaseText(stored.Description, baseLanguage) is var storedDescription && !BaseTextMatches(storedDescription, description))
            mismatches.Add($"description: requested '{description}' (LCID {baseLanguage}), stored '{storedDescription ?? "none"}'");
        if (hasColor && !ColorsMatch(stored.Color, color!))
            mismatches.Add($"color: requested '{color}', stored '{stored.Color ?? "none"}'");
        if (mismatches.Count > 0)
            throw new PpdsException(MetadataErrorCodes.WriteNotConfirmed,
                $"{written} Dataverse did not store its {string.Join("; its ", mismatches)}.{notPublished} Check the option before re-running the command.");
        _logger?.LogInformation("Confirmed {Parts} on option {Value}", string.Join(", ", parts), value);
    }

    /// <summary>The base-language text of <paramref name="label"/>, or null when it has no base-language label.</summary>
    private static string? BaseText(Label? label, int baseLanguage)
        => label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == baseLanguage)?.Label;

    /// <summary>Exact (ordinal) match; an empty requested text is matched by an empty or absent label.</summary>
    private static bool BaseTextMatches(string? stored, string requested)
        => requested.Length == 0 ? string.IsNullOrEmpty(stored) : string.Equals(stored, requested, StringComparison.Ordinal);

    private static string ConfirmationReadFailure(Exception ex) => ex switch
    {
        TaskCanceledException => "the read timed out or was cancelled",
        OperationCanceledException => "the read was cancelled",
        PpdsException or MetadataValidationException => ex.Message.TrimEnd('.'),
        _ => "the read failed"
    };

    /// <summary>Reads one table's entity-level metadata as-if-published, to confirm a label write.</summary>
    private Task<EntityMetadata> ReadTableForConfirmationAsync(string entityLogicalName, CancellationToken ct)
        => ReadForConfirmationAsync(new RetrieveEntityRequest { LogicalName = entityLogicalName, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true },
            r => (r as RetrieveEntityResponse)?.EntityMetadata, $"table '{entityLogicalName}'", ct);

    /// <summary>Reads one column as-if-published, to confirm a label write.</summary>
    private Task<AttributeMetadata> ReadColumnForConfirmationAsync(string entityLogicalName, string columnLogicalName, CancellationToken ct)
        => ReadForConfirmationAsync(new RetrieveAttributeRequest { EntityLogicalName = entityLogicalName, LogicalName = columnLogicalName, RetrieveAsIfPublished = true },
            r => (r as RetrieveAttributeResponse)?.AttributeMetadata, $"column '{entityLogicalName}.{columnLogicalName}'", ct);

    /// <summary>Reads one global choice under its stored name as-if-published, to confirm a label write.</summary>
    private Task<OptionSetMetadataBase> ReadChoiceForConfirmationAsync(string name, CancellationToken ct)
        => ReadForConfirmationAsync(new RetrieveOptionSetRequest { Name = name, RetrieveAsIfPublished = true },
            r => (r as RetrieveOptionSetResponse)?.OptionSetMetadata, $"global choice '{name}'", ct);

    private async Task<T> ReadForConfirmationAsync<T>(OrganizationRequest request, Func<OrganizationResponse, T?> pick, string what, CancellationToken ct)
        where T : class
    {
        OrganizationResponse response;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            try
            {
                response = await client.ExecuteAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
            {
                throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, $"Failed to read {what} to confirm the write.", ex);
            }
        }

        return pick(response) ?? throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, $"Dataverse returned no metadata for {what}.");
    }
}
