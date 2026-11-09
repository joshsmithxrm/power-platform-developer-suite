using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.Forms;

public sealed partial class FormService
{
    public Task ReorderFieldsStrictAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
        => EditFieldLayoutAsync(request.EntityLogicalName, request.FormName, request.SolutionUniqueName, request.Publish,
            request.FieldLogicalNames, (doc, fields) => FormXmlEditor.ReorderFieldsStrict(doc, request.SectionLabel, fields), reporter, ct);

    public Task MoveFieldAsync(MoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
        => EditFieldLayoutAsync(request.EntityLogicalName, request.FormName, request.SolutionUniqueName, request.Publish,
            request.BeforeField is null ? [request.FieldLogicalName] : [request.FieldLogicalName, request.BeforeField],
            (doc, fields) => FormXmlEditor.MoveField(doc, request, fields[0], fields.Length == 1 ? null : fields[1]), reporter, ct);

    private async Task EditFieldLayoutAsync(string entity, string form, string? solution, bool publish,
        string[] requested, Func<XDocument, string[], bool> edit, IProgressReporter? reporter, CancellationToken ct)
    {
        await _confirmation.EnsureConfirmedAsync("forms mutation", cancellationToken: ct).ConfigureAwait(false);
        if (requested.Length == 0 || requested.Any(string.IsNullOrWhiteSpace))
            throw new PpdsValidationException("FieldLogicalNames", "At least one nonempty field name is required.");
        try
        {
            var (logicalName, xml, formId, _, _, _, _) = await FetchFormRecordAsync(entity, form, true, ct, requireCustomizable: true);
            var attributes = (await _metadataService.GetAttributesAsync(logicalName, cancellationToken: ct))
                .ToDictionary(attribute => attribute.LogicalName, StringComparer.OrdinalIgnoreCase);
            var canonical = requested.Select(field => attributes.TryGetValue(field, out var metadata) ? metadata.LogicalName
                : throw new PpdsException(FormErrorCodes.ColumnNotFound, $"Column '{field}' not found on entity '{logicalName}'.")).ToArray();
            var doc = XDocument.Parse(xml!);
            FormXmlValidator.Validate(doc);
            ct.ThrowIfCancellationRequested();
            var changed = edit(doc, canonical);
            FormXmlValidator.Validate(doc);
            ct.ThrowIfCancellationRequested();
            if (changed)
            {
                reporter?.ReportPhase("Writing", "Updating field layout");
                ct.ThrowIfCancellationRequested();
                await WriteFormXmlAsync(doc, formId, logicalName, solution, publish, ct);
            }
            else
            {
                reporter?.ReportPhase("Unchanged", "The requested field layout is already present.");
                ct.ThrowIfCancellationRequested();
                await ApplySideEffectsAsync(formId, logicalName, solution, publish, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml, $"Failed to update field layout on form '{form}': {ex.Message}", ex);
        }
    }
}
