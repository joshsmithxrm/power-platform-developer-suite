using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Resilience;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Global choice name resolution (#1535). Dataverse stores global choice names in their canonical
/// (usually lower-case) form, while callers naturally reuse the spelling they created the choice with.
/// </summary>
public partial class DataverseMetadataAuthoringService
{
    /// <summary>
    /// Resolves the stored name of a global picklist that a Choice/Choices column can attach to.
    /// An exact match wins; otherwise a single case-insensitive match is accepted; anything else fails
    /// before any write.
    /// </summary>
    private async Task<string> ResolveGlobalChoiceNameAsync(string name, CancellationToken ct)
    {
        var definitions = await ListGlobalChoiceDefinitionsAsync(name, ct).ConfigureAwait(false);
        var selected = SelectGlobalChoice(definitions, name, nameof(CreateColumnRequest.OptionSetName));
        if (selected is not OptionSetMetadata || selected.IsGlobal == false
            || selected.OptionSetType is not (null or OptionSetType.Picklist))
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"'{selected.Name}' is not a global picklist that can be attached to a Choice or Choices column.",
                nameof(CreateColumnRequest.OptionSetName));

        return selected.Name;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with the caller's spelling of a global choice name. The exact
    /// spelling is always tried first and its outcome stands, with one exception: when Dataverse rejects it
    /// with a "not found" fault (see <see cref="IsOptionSetNotFoundFault"/>), the catalog is listed. If exactly one stored name differs from
    /// the request only by case, the operation is retried once under that stored name. If several do, the
    /// existing ambiguity error is raised (with the original fault as its inner exception). In every other
    /// case (no case variant, the requested spelling is itself stored, or the lookup fails) the original
    /// exception is rethrown unchanged.
    /// </summary>
    /// <remarks>
    /// Recognition is by fault code, so any ObjectDoesNotExist fault qualifies, including one about another object
    /// (for example a missing solution). That costs one extra as-if-published catalog listing (every global choice
    /// with its options) before the original error returns; the write is re-sent once only when a single stored
    /// case variant of the requested name exists, and that retry's own failure surfaces. Every other failure
    /// (privilege, duplicate value, validation, managed property, plugin, timeout, throttle, connection,
    /// cancellation, and faults with other codes and no English not-found wording) propagates untouched with no
    /// listing and no retry. The design assumes, pending live confirmation, that a not-found fault means nothing
    /// was written under the first spelling.
    /// Each attempt and the catalog lookup acquire and release their own pooled lease.
    /// </remarks>
    /// <returns>The name the successful attempt used (the stored name when Dataverse reports it) and its result.</returns>
    private async Task<(string Name, T Result)> WithGlobalChoiceNameAsync<T>(
        string name,
        string field,
        Func<string, Task<T>> operation,
        Func<T, string?>? storedName,
        CancellationToken ct)
    {
        ExceptionDispatchInfo original;
        try
        {
            var result = await operation(name).ConfigureAwait(false);
            return (StoredNameOrRequested(name, storedName?.Invoke(result)), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsAuthException
            and not MetadataValidationException && IsOptionSetNotFoundFault(ex, name))
        {
            original = ExceptionDispatchInfo.Capture(ex);
        }

        var canonical = await TryResolveCaseVariantAsync(name, field, original.SourceException, ct).ConfigureAwait(false);
        if (canonical is null)
            original.Throw();

        var retried = await operation(canonical!).ConfigureAwait(false);
        return (StoredNameOrRequested(canonical!, storedName?.Invoke(retried)), retried);
    }

    /// <summary>Retrieves a global choice by name, falling back to canonical resolution (#1535).</summary>
    private Task<(string Name, RetrieveOptionSetResponse Response)> RetrieveGlobalChoiceAsync(
        string name, string field, bool retrieveAsIfPublished, CancellationToken ct)
        => WithGlobalChoiceNameAsync(name, field, async candidate =>
        {
            var request = new RetrieveOptionSetRequest { Name = candidate, RetrieveAsIfPublished = retrieveAsIfPublished };
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            return (RetrieveOptionSetResponse)await client.ExecuteAsync(request, ct).ConfigureAwait(false);
        }, response => response?.OptionSetMetadata?.Name, ct);

    /// <summary>
    /// Returns the stored spelling when exactly one stored name differs from <paramref name="name"/> only by
    /// case and none matches it exactly; otherwise null, meaning the original failure stands. Throws the
    /// existing ambiguity error (carrying <paramref name="original"/>) when several case variants exist.
    /// </summary>
    private async Task<string?> TryResolveCaseVariantAsync(string name, string field, Exception original, CancellationToken ct)
    {
        OptionSetMetadataBase[] definitions;
        try
        {
            definitions = await ListGlobalChoiceDefinitionsAsync(name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // The caller's cancellation always propagates.
        }
        catch (Exception ex) when (ex is not MetadataValidationException)
        {
            // Includes an OperationCanceledException not raised by the caller's token (e.g. an HTTP-layer
            // timeout): the lookup failure must never replace the original not-found fault.
            _logger?.LogDebug(ex, "Could not list global choices to resolve {Name}; the original failure stands", name);
            return null;
        }

        var matches = definitions.Where(d => string.Equals(d?.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 || matches.Any(d => string.Equals(d.Name, name, StringComparison.Ordinal)))
            return null;
        if (matches.Length > 1)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"Global choice '{name}' matched multiple definitions. Check the exact stored name.",
                field, original);

        _logger?.LogDebug("Global choice {Name} resolved to stored name {StoredName}", name, matches[0].Name);
        return matches[0].Name;
    }

    private async Task<OptionSetMetadataBase[]> ListGlobalChoiceDefinitionsAsync(string name, CancellationToken ct)
    {
        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            var response = await client.ExecuteAsync(
                new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = true }, ct).ConfigureAwait(false);
            return (response as RetrieveAllOptionSetsResponse)?.OptionSetMetadata
                ?? throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                    "Dataverse did not return the global choice definitions.");
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException and not OperationCanceledException)
        {
            var (error, exitCode) = ExceptionMapper.MapWithExitCode(ex);
            if (exitCode == ExitCodes.AuthError)
                throw new PpdsAuthException(error.Code, $"Could not retrieve the global choices to resolve '{name}'.", ex)
                {
                    RequiresReauthentication = ex is DataverseAuthenticationException { RequiresReauthentication: true }
                };

            var code = exitCode is ExitCodes.ConnectionError or ExitCodes.InvalidArguments
                ? error.Code
                : MetadataErrorCodes.SdkOperationFailed;
            throw new PpdsException(code, $"Could not retrieve the global choices to resolve '{name}'.", ex);
        }
    }

    private static OptionSetMetadataBase SelectGlobalChoice(OptionSetMetadataBase[] definitions, string name, string field)
    {
        var matches = definitions.Where(d => string.Equals(d?.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = matches.Where(d => string.Equals(d.Name, name, StringComparison.Ordinal)).ToArray();
        var candidates = exact.Length > 0 ? exact : matches;
        if (candidates.Length == 0)
            throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Global choice '{name}' was not found. List the available choices with 'ppds metadata optionsets'.",
                field);
        if (candidates.Length > 1)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"Global choice '{name}' matched multiple definitions. Check the exact stored name.",
                field);
        return candidates[0];
    }

    /// <summary>An entity + attribute pair fully identifies a column-scoped option set (see VerifyOptionColorAsync).</summary>
    private static bool IsLocalOptionTarget(string? entityLogicalName, string? attributeLogicalName)
        => !string.IsNullOrEmpty(entityLogicalName) && !string.IsNullOrEmpty(attributeLogicalName);

    private static string StoredNameOrRequested(string requested, string? stored)
        => !string.IsNullOrEmpty(stored) && string.Equals(stored, requested, StringComparison.OrdinalIgnoreCase)
            ? stored
            : requested;

    /// <summary>
    /// Dataverse's language-independent "object not found" fault code (ObjectDoesNotExist, 0x80040217). ASSUMPTION,
    /// pending live confirmation: Microsoft does not document which code an unknown global choice name returns; this
    /// is the generic not-found code the offline fakes model (see the live test
    /// MetadataGlobalChoiceLiveTests). Keep in step with DataverseMetadataQueryService.
    /// </summary>
    private const int ObjectDoesNotExistErrorCode = unchecked((int)0x80040217);

    /// <summary>
    /// The anchored phrases of Dataverse's "option set not found" fault, kept as a fallback for a fault that
    /// carries a different code: Microsoft does not document which code this message uses, while the English
    /// wording is recorded from a real server, "Could not find an optionset with name statuscode"
    /// (docs/qa/shakedown-v1-2026-04-20.md); the spaced variant covers the same message family. Keep in step
    /// with the copy in DataverseMetadataQueryService.
    /// </summary>
    private static readonly string[] OptionSetNotFoundPhrases =
        ["Could not find an optionset with name", "Could not find an option set with"];

    /// <summary>
    /// The wording Dataverse returned in the field report for #1535. It echoes no name, so it can only be about
    /// the option set the request itself named. Keep in step with the copy in DataverseMetadataQueryService.
    /// </summary>
    private const string OptionSetNotFoundUnnamedPhrase = "Could not find an option set with specified name";

    /// <summary>
    /// True for a Dataverse "not found" fault that may be about <paramref name="requestedName"/>. The typed fault
    /// code decides, so recognition does not depend on the organisation's language: any ObjectDoesNotExist fault
    /// qualifies. A fault with another code qualifies only through the English fallback: the unnamed wording, or
    /// an anchored phrase (ordinal, case-insensitive) AND the requested name. Qualifying only lists the catalog;
    /// a retry still needs a single stored case variant of the requested name, and its own failure surfaces.
    /// </summary>
    private static bool IsOptionSetNotFoundFault(Exception exception, string requestedName)
    {
        var fault = FindTypedFault(exception);
        return fault is not null
            && (fault.Detail?.ErrorCode == ObjectDoesNotExistErrorCode
                || IsOptionSetNotFoundText(fault.Detail?.Message, requestedName) || IsOptionSetNotFoundText(fault.Message, requestedName));
    }

    private static bool IsOptionSetNotFoundText(string? text, string requestedName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(requestedName))
            return false;
        if (text.Contains(OptionSetNotFoundUnnamedPhrase, StringComparison.OrdinalIgnoreCase))
            return true;
        return OptionSetNotFoundPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
               && text.Contains(requestedName, StringComparison.OrdinalIgnoreCase);
    }
}
