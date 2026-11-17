using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.Forms;

public sealed partial class FormService
{
    public async Task<RepairBindingsResult> RepairBindingsAsync(RepairBindingsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        // A dry run plans only: like MCP draft dry runs it needs no target acknowledgement and makes no request that writes.
        if (!request.DryRun)
            await _confirmation.EnsureConfirmedAsync("forms mutation", cancellationToken: ct).ConfigureAwait(false);
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, resolvedName, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();

            var doc = XDocument.Parse(formXmlStr!);
            var (repaired, unchanged) = FormXmlEditor.RepairBindings(doc, attributes.Select(a => a.LogicalName));
            // Validate the planned XML on every path, so a dry run surfaces what the write would reject.
            FormXmlValidator.Validate(doc);
            ct.ThrowIfCancellationRequested();

            if (request.DryRun)
            {
                reporter?.ReportPhase("Planned", $"{repaired.Count} binding(s) would be repaired; nothing was written.");
                return new(formId, resolvedName, true, repaired, unchanged, false, false, false);
            }

            if (repaired.Count > 0)
            {
                reporter?.ReportPhase("Writing", $"Repairing {repaired.Count} binding(s)");
                ct.ThrowIfCancellationRequested();
                await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
            }
            else
            {
                reporter?.ReportPhase("Unchanged", "No binding differs from its column's logical name only by case.");
                ct.ThrowIfCancellationRequested();
                // Explicit solution/publication requests keep their established effects, without an XML update.
                await ApplySideEffectsAsync(formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
            }

            return new(formId, resolvedName, false, repaired, unchanged, repaired.Count > 0,
                request.SolutionUniqueName is not null, request.Publish);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to repair bindings on form '{request.FormName}': {ex.Message}", ex);
        }
    }
}
