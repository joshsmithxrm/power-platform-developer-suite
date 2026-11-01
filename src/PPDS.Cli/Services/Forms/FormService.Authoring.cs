using System.Security.Cryptography;
using System.ServiceModel;
using System.Text;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.Forms;

public sealed partial class FormService
{
    public static string ComputeDraftHash(string xml)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();

    public async Task<FormAuthoringResult> AddFieldDraftAsync(
        AddFieldDraftRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        ValidateAuthoringTarget(request.EntityLogicalName, request.FormId, request.ExpectedDraftHash);
        if (request.SectionId == Guid.Empty)
            throw new PpdsValidationException("SectionId", "A stable section ID is required.");
        if (string.IsNullOrWhiteSpace(request.FieldLogicalName))
            throw new PpdsValidationException("FieldLogicalName", "An existing column logical name is required.");
        if (!request.DryRun)
            await _confirmation.EnsureConfirmedAsync("add form field draft", request.ConfirmEnvironment, ct).ConfigureAwait(false);

        using var lease = await AcquireAuthoringLockAsync(request.FormId, ct).ConfigureAwait(false);
        reporter?.ReportPhase("Planning", "Reading the unpublished form and column metadata");
        try
        {
            var form = await FetchFormEntityAsync(request.EntityLogicalName, request.FormId.ToString(), true, ct,
                requireCustomizable: true).ConfigureAwait(false);
            var xml = RequireFormXml(form);
            RequireExpectedHash(xml, request.ExpectedDraftHash);
            var doc = XDocument.Parse(xml);
            var section = FormXmlEditor.RequireSectionById(doc, request.SectionId);
            var attributes = await _metadataService.GetAttributesAsync(request.EntityLogicalName, cancellationToken: ct).ConfigureAwait(false);
            var attribute = attributes.SingleOrDefault(a => a.LogicalName.Equals(request.FieldLogicalName, StringComparison.OrdinalIgnoreCase))
                ?? throw new PpdsException(FormErrorCodes.ColumnNotFound, $"Column '{request.FieldLogicalName}' not found on '{request.EntityLogicalName}'.");
            var classId = ClassIdResolver.ResolveForField(attribute.AttributeType);
            var present = section.Descendants("control").Any(c =>
                string.Equals((string?)c.Attribute("datafieldname"), attribute.LogicalName, StringComparison.OrdinalIgnoreCase));
            if (!present)
                FormXmlEditor.AddField(doc, request.SectionId.ToString(), attribute.LogicalName, classId, attribute.DisplayName);
            FormXmlValidator.Validate(doc);
            var associate = await NeedsSolutionAssociationAsync(request.FormId, request.SolutionUniqueName, ct).ConfigureAwait(false);
            var result = NewResult(request.EntityLogicalName, request.FormId) with
            {
                DryRun = request.DryRun, SectionId = request.SectionId, FieldLogicalName = attribute.LogicalName,
                SolutionUniqueName = request.SolutionUniqueName, DraftHash = ComputeDraftHash(xml),
                DraftWrite = new(present ? "unchanged" : "not-attempted"),
                SolutionAssociation = new(associate ? "not-attempted" : "unchanged")
            };
            if (request.DryRun)
            {
                result.DraftWrite = new(present ? "unchanged" : "would-change");
                result.SolutionAssociation = new(associate ? "would-change" : "unchanged");
                return result;
            }

            // Re-read immediately before submission; this also detects a cooperating writer's change.
            var current = await FetchFormEntityAsync(request.EntityLogicalName, request.FormId.ToString(), true, ct,
                requireCustomizable: true).ConfigureAwait(false);
            RequireExpectedHash(RequireFormXml(current), request.ExpectedDraftHash);
            var expected = doc.ToString(SaveOptions.DisableFormatting);
            if (!present)
            {
                var optimistic = await SupportsOptimisticConcurrencyAsync(ct).ConfigureAwait(false)
                    && !string.IsNullOrWhiteSpace(current.RowVersion);
                result.ConcurrencyMode = optimistic ? "row-version" : "preflight-only";
                if (optimistic) result.Warnings = [];
                reporter?.ReportPhase("Writing", "Saving unpublished form XML");
                try
                {
                    await _confirmation.EnsureConfirmedAsync("save form draft", cancellationToken: ct).ConfigureAwait(false);
                    await using var client = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
                    var update = new Entity("systemform", request.FormId) { ["formxml"] = expected };
                    if (optimistic)
                    {
                        update.RowVersion = current.RowVersion;
                        await client.ExecuteAsync(new UpdateRequest { Target = update, ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches }, ct).ConfigureAwait(false);
                    }
                    else await client.UpdateAsync(update, ct).ConfigureAwait(false);
                    result.DraftWrite = new("succeeded");
                }
                catch (Exception ex)
                {
                    result.DraftWrite = SubmissionFailure(ex);
                    result.Status = "partial";
                    return result;
                }
            }

            if (associate)
            {
                reporter?.ReportPhase("Associating", "Adding the form to the requested solution");
                try
                {
                    await _confirmation.EnsureConfirmedAsync("associate form with solution", cancellationToken: ct).ConfigureAwait(false);
                    await ApplySideEffectsAsync(request.FormId, request.EntityLogicalName, request.SolutionUniqueName, false, ct).ConfigureAwait(false);
                    result.SolutionAssociation = new("succeeded");
                }
                catch (Exception ex)
                {
                    result.SolutionAssociation = SubmissionFailure(ex);
                    result.Status = "partial";
                    return result;
                }
            }

            reporter?.ReportPhase("Verifying", "Reading back the unpublished form");
            try
            {
                var observed = await FetchFormEntityAsync(request.EntityLogicalName, request.FormId.ToString(), true, ct).ConfigureAwait(false);
                var observedXml = RequireFormXml(observed);
                VerifyEquivalentForm(expected, observedXml);
                result.DraftHash = ComputeDraftHash(observedXml);
                result.DraftVerification = new("succeeded");
                result.Status = present && !associate ? "unchanged" : "saved-unpublished";
            }
            catch (Exception ex)
            {
                result.DraftVerification = ReadbackFailure(ex);
                result.Status = "partial";
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException("Forms.AuthoringFailed", "Could not prepare the form field change.", ex);
        }
    }

    public async Task<FormAuthoringResult> PublishFormEntityAsync(
        PublishFormEntityRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        ValidateAuthoringTarget(request.EntityLogicalName, request.FormId, request.ExpectedDraftHash);
        if (!string.Equals(request.EntityLogicalName, request.ConfirmPublishEntity, StringComparison.Ordinal))
            throw new PpdsValidationException("ConfirmPublishEntity",
                "Confirm the exact table logical name. Publication includes all pending customizations for that table.");
        await _confirmation.EnsureConfirmedAsync("publish table customizations", request.ConfirmEnvironment, ct).ConfigureAwait(false);
        using var lease = await AcquireAuthoringLockAsync(request.FormId, ct).ConfigureAwait(false);
        try
        {
            reporter?.ReportPhase("Planning", "Verifying the selected unpublished form");
            var form = await FetchFormEntityAsync(request.EntityLogicalName, request.FormId.ToString(), true, ct).ConfigureAwait(false);
            var expected = RequireFormXml(form);
            RequireExpectedHash(expected, request.ExpectedDraftHash);
            var result = NewResult(request.EntityLogicalName, request.FormId) with
            {
                PublicationScope = "entity",
                DraftHash = ComputeDraftHash(expected),
                Warnings = ["Publication includes other pending customizations for this table. Only the selected form is verified."]
            };
            reporter?.ReportPhase("Publishing", $"Publishing pending customizations for {request.EntityLogicalName}");
            try
            {
                await _confirmation.EnsureConfirmedAsync("publish table customizations", cancellationToken: ct).ConfigureAwait(false);
                await ApplySideEffectsAsync(request.FormId, request.EntityLogicalName, null, true, ct).ConfigureAwait(false);
                result.EntityPublication = new("succeeded");
            }
            catch (Exception ex)
            {
                result.EntityPublication = SubmissionFailure(ex);
                result.Status = "partial";
                return result;
            }
            reporter?.ReportPhase("Verifying", "Reading the published form");
            try
            {
                var observed = await FetchFormEntityAsync(request.EntityLogicalName, request.FormId.ToString(), true, ct, unpublished: false).ConfigureAwait(false);
                VerifyEquivalentForm(expected, RequireFormXml(observed));
                result.PublishedVerification = new("succeeded");
                result.Status = "published";
            }
            catch (Exception ex)
            {
                result.PublishedVerification = ReadbackFailure(ex);
                result.Status = "partial";
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException("Forms.PublicationFailed", "Could not prepare table publication.", ex);
        }
    }

    private Task<IDisposable> AcquireAuthoringLockAsync(Guid formId, CancellationToken ct)
        => (_mutations ?? throw new PpdsException("Forms.ContextRequired", "Form authoring requires a session mutation coordinator."))
            .AcquireAsync(_confirmation.Context.EnvironmentUrl, formId, ct);

    private FormAuthoringResult NewResult(string entity, Guid formId) => new()
    {
        EnvironmentUrl = _confirmation.Context.EnvironmentUrl, EntityLogicalName = entity, FormId = formId,
        Warnings = ["Preflight fingerprint checks cannot prevent concurrent changes from other processes or Maker. Avoid concurrent editors."]
    };

    private static void ValidateAuthoringTarget(string entity, Guid formId, string hash)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new PpdsValidationException("EntityLogicalName", "A table logical name is required.");
        if (formId == Guid.Empty) throw new PpdsValidationException("FormId", "A stable form ID is required.");
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new PpdsValidationException("ExpectedDraftHash", "Use the draft fingerprint returned by forms get with unpublished=true.");
    }

    private static string RequireFormXml(Entity form)
        => form.GetAttributeValue<string>("formxml")
            ?? throw new PpdsException(FormErrorCodes.InvalidFormXml, "The form has no XML definition.");

    private static void RequireExpectedHash(string xml, string expected)
    {
        if (!string.Equals(ComputeDraftHash(xml), expected, StringComparison.OrdinalIgnoreCase))
            throw new PpdsException("Forms.StaleDraft", "The unpublished form changed. Inspect the current draft before retrying.");
    }

    private static void VerifyEquivalentForm(string expected, string observed)
    {
        if (!XNode.DeepEquals(XDocument.Parse(expected), XDocument.Parse(observed)))
            throw new PpdsException("Forms.VerificationFailed", "Form readback differs from the intended structure. Inspect the form before retrying.");
    }

    private async Task<bool> SupportsOptimisticConcurrencyAsync(CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (RetrieveEntityResponse)await client.ExecuteAsync(new RetrieveEntityRequest
        {
            LogicalName = "systemform", EntityFilters = EntityFilters.Entity
        }, ct).ConfigureAwait(false);
        return response.EntityMetadata.IsOptimisticConcurrencyEnabled == true;
    }

    private async Task<bool> NeedsSolutionAssociationAsync(Guid formId, string? solutionName, CancellationToken ct)
    {
        if (solutionName is null) return false;
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new PpdsValidationException("SolutionUniqueName", "Solution unique name cannot be empty.");
        EntityCollection solutions;
        await using (var client = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            var query = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid", "ismanaged"), TopCount = 2 };
            query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);
            solutions = await client.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
        }
        if (solutions.Entities.Count != 1 || solutions.Entities[0].GetAttributeValue<bool>("ismanaged"))
            throw new PpdsException("Forms.InvalidSolution", "The requested solution must resolve to one unmanaged solution.");
        await using var membershipClient = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var membership = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("solutioncomponentid"), TopCount = 1 };
        membership.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutions.Entities[0].Id);
        membership.Criteria.AddCondition("objectid", ConditionOperator.Equal, formId);
        membership.Criteria.AddCondition("componenttype", ConditionOperator.Equal, SystemFormComponentType);
        return (await membershipClient.RetrieveMultipleAsync(membership, ct).ConfigureAwait(false)).Entities.Count == 0;
    }

    private static FormPhaseResult SubmissionFailure(Exception ex)
        => new(ex is FaultException<OrganizationServiceFault> or PpdsException ? "failed" : "unknown",
            ex is PpdsException ppds ? ppds.ErrorCode : ex is OperationCanceledException ? "Operation.Cancelled" : "Forms.SubmissionFailed",
            ex is OperationCanceledException ? "Cancelled after entering the write phase; inspect state before retrying." : ex.Message);

    private static FormPhaseResult ReadbackFailure(Exception ex)
        => new("failed", ex is PpdsException ppds ? ppds.ErrorCode : ex is OperationCanceledException ? "Operation.Cancelled" : "Forms.VerificationFailed",
            ex is OperationCanceledException ? "Verification cancelled. Earlier successful phases remain applied." : ex.Message);
}

