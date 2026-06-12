using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// IDataverseConnectionPool-backed implementation of <see cref="IFormService"/>.
/// </summary>
public sealed class FormService : IFormService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IMetadataQueryService _metadataService;
    private readonly ILogger<FormService> _logger;

    private const int MainFormType = 2;
    private const int SystemFormComponentType = 60;

    public FormService(
        IDataverseConnectionPool pool,
        IMetadataQueryService metadataService,
        ILogger<FormService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Read ──────────────────────────────────────────────────────────────

    public async Task<ListResult<FormInfo>> ListAsync(string entityLogicalName, CancellationToken ct = default)
    {
        try
        {
            var query = new QueryExpression("systemform")
            {
                ColumnSet = new ColumnSet("formid", "name", "type", "ismanaged", "description"),
                Criteria = new FilterExpression(LogicalOperator.And)
            };
            query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, entityLogicalName);
            query.Criteria.AddCondition("type", ConditionOperator.NotNull);

            await using var client = await _pool.GetClientAsync(cancellationToken: ct);
            var result = await client.RetrieveMultipleAsync(query, ct);

            var items = result.Entities
                .Select(e => new FormInfo(
                    e.GetAttributeValue<Guid>("formid"),
                    e.GetAttributeValue<string>("name") ?? string.Empty,
                    e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0,
                    FormTypeName(e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0),
                    e.GetAttributeValue<bool>("ismanaged"),
                    e.GetAttributeValue<string>("description")))
                .ToList();

            return new ListResult<FormInfo>
            {
                Items = items,
                TotalCount = items.Count
            };
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.EntityNotFound,
                $"Failed to list forms for entity '{entityLogicalName}': {ex.Message}", ex);
        }
    }

    public async Task<FormDetail?> GetAsync(string entityLogicalName, string formName, bool unpublished = false, CancellationToken ct = default)
    {
        try
        {
            var (_, formXmlStr, formId, resolvedName, formType, isManaged, description) =
                await FetchFormRecordAsync(entityLogicalName, formName, requireMainForm: false, ct, unpublished: unpublished);

            if (formXmlStr is null) return null;

            var doc = XDocument.Parse(formXmlStr);
            var tabs = ParseTabs(doc);

            return new FormDetail(
                formId,
                resolvedName,
                formType,
                FormTypeName(formType),
                isManaged,
                description,
                tabs);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Failed to get form '{formName}' for entity '{entityLogicalName}': {ex.Message}", ex);
        }
    }

    // ── Set XML ───────────────────────────────────────────────────────────

    public async Task SetFormXmlAsync(SetFormXmlRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, _, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: false, ct, requireCustomizable: true);

            reporter?.ReportPhase("Validating", "Validating form XML");
            var doc = XDocument.Parse(request.FormXml);
            FormXmlValidator.Validate(doc);

            reporter?.ReportPhase("Writing", "Updating systemform record");
            // D2: acquire, use, release — one client per operation
            await using (var writeClient = await _pool.GetClientAsync(cancellationToken: ct))
            {
                var entity = new Entity("systemform", formId)
                {
                    ["formxml"] = request.FormXml
                };
                await writeClient.UpdateAsync(entity, ct);
            }

            await ApplySideEffectsAsync(formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to set form XML for '{request.FormName}': {ex.Message}", ex);
        }
    }

    // ── Tab management ────────────────────────────────────────────────────

    public async Task<TabResult> AddTabAsync(AddTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.AddTab(doc, request);

            var newTabId = doc.Descendants("tab").Last()
                .Attribute("id")?.Value ?? string.Empty;

            reporter?.ReportPhase("Writing", $"Adding tab '{request.Label}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);

            return new TabResult(Guid.TryParse(newTabId.Trim('{', '}'), out var g) ? g : Guid.Empty, request.Label);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to add tab to form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task UpdateTabAsync(UpdateTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.UpdateTab(doc, request);

            reporter?.ReportPhase("Writing", $"Updating tab '{request.TabLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to update tab '{request.TabLabel}' on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task RemoveTabAsync(RemoveTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.RemoveTab(doc, request.TabLabel);

            reporter?.ReportPhase("Writing", $"Removing tab '{request.TabLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to remove tab '{request.TabLabel}' from form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task<TabInfo?> FindTabAsync(FindTabRequest request, CancellationToken ct = default)
    {
        try
        {
            var (_, formXmlStr, _, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct);

            var doc = XDocument.Parse(formXmlStr!);
            var tabs = doc.Descendants("tab").ToList();

            for (var i = 0; i < tabs.Count; i++)
            {
                if (!FormXmlEditor.ElementMatches(tabs[i], request.TabLabel))
                    continue;

                var idStr = (string?)tabs[i].Attribute("id") ?? string.Empty;
                Guid.TryParse(idStr.Trim('{', '}'), out var tabId);
                var label = (string?)tabs[i].Element("labels")?.Element("label")?.Attribute("description") ?? request.TabLabel;
                return new TabInfo(tabId, label, i);
            }

            return null;
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Failed to find tab '{request.TabLabel}' on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    // ── Section management ────────────────────────────────────────────────

    public async Task<SectionResult> AddSectionAsync(AddSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.AddSection(doc, request);

            var newSection = doc.Descendants("section").Last();
            var sectionIdStr = (string?)newSection.Attribute("id") ?? string.Empty;
            Guid.TryParse(sectionIdStr.Trim('{', '}'), out var sectionId);

            reporter?.ReportPhase("Writing", $"Adding section '{request.Label}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);

            return new SectionResult(sectionId, request.Label, request.TabLabel);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to add section to form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task UpdateSectionAsync(UpdateSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.UpdateSection(doc, request);

            reporter?.ReportPhase("Writing", $"Updating section '{request.SectionLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to update section '{request.SectionLabel}' on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task RemoveSectionAsync(RemoveSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.RemoveSection(doc, request.SectionLabel);

            reporter?.ReportPhase("Writing", $"Removing section '{request.SectionLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to remove section '{request.SectionLabel}' from form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task<SectionInfo?> FindSectionAsync(FindSectionRequest request, CancellationToken ct = default)
    {
        try
        {
            var (_, formXmlStr, _, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct);

            var doc = XDocument.Parse(formXmlStr!);

            foreach (var tab in doc.Descendants("tab"))
            {
                var tabLabelAttr = tab.Element("labels")?.Element("label")?.Attribute("description");
                var tabLabel = (string?)tabLabelAttr ?? string.Empty;
                var tabIdStr = (string?)tab.Attribute("id") ?? string.Empty;
                Guid.TryParse(tabIdStr.Trim('{', '}'), out var tabId);

                foreach (var section in tab.Descendants("section"))
                {
                    if (!FormXmlEditor.ElementMatches(section, request.SectionLabel))
                        continue;

                    var sectionLabelAttr = section.Element("labels")?.Element("label")?.Attribute("description");
                    var sectionLabel = (string?)sectionLabelAttr ?? request.SectionLabel;
                    var sectionIdStr = (string?)section.Attribute("id") ?? string.Empty;
                    Guid.TryParse(sectionIdStr.Trim('{', '}'), out var sectionId);
                    return new SectionInfo(sectionId, sectionLabel, tabLabel, tabId);
                }
            }

            return null;
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Failed to find section '{request.SectionLabel}' on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    // ── Field management ──────────────────────────────────────────────────

    public async Task AddFieldAsync(AddFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        if (request.FieldLogicalNames.Length == 0)
            throw new PpdsValidationException("FieldLogicalNames", "At least one field is required.");

        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct);
            var attrByName = attributes.ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

            var doc = XDocument.Parse(formXmlStr!);

            foreach (var field in request.FieldLogicalNames)
            {
                if (!attrByName.TryGetValue(field, out var attr))
                    throw new PpdsException(FormErrorCodes.ColumnNotFound,
                        $"Column '{field}' not found on entity '{entityLogicalName}'.");

                var classId = ClassIdResolver.ResolveForField(attr.AttributeType);
                FormXmlEditor.AddField(doc, request.SectionLabel, field, classId, attr.DisplayName);
            }

            reporter?.ReportPhase("Writing", $"Adding {request.FieldLogicalNames.Length} field(s) to section '{request.SectionLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to add fields to form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task RemoveFieldAsync(RemoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.RemoveField(doc, request.FieldLogicalName);

            reporter?.ReportPhase("Writing", $"Removing field '{request.FieldLogicalName}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to remove field '{request.FieldLogicalName}' from form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task ReorderFieldsAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        if (request.FieldLogicalNames.Length == 0)
            throw new PpdsValidationException("FieldLogicalNames", "At least one field is required for reorder-fields.");

        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct);
            var classIdsByField = attributes
                .Where(a => ClassIdCanBeResolved(a.AttributeType))
                .ToDictionary(a => a.LogicalName, a => ClassIdResolver.ResolveForField(a.AttributeType), StringComparer.OrdinalIgnoreCase);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.ReorderFields(doc, request.SectionLabel, request.FieldLogicalNames, classIdsByField);

            reporter?.ReportPhase("Writing", $"Reordering fields in section '{request.SectionLabel}'");
            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to reorder fields on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    // ── Sub-grid management ───────────────────────────────────────────────

    public async Task AddSubgridAsync(AddSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        if (request.MaxRows < 2 || request.MaxRows > 250)
            throw new PpdsException(FormErrorCodes.InvalidMaxRows,
                $"--max-rows must be between 2 and 250 (got {request.MaxRows}).");

        try
        {
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            // Validate the view GUID corresponds to an existing savedqueries record.
            // Scope the pooled client to this lookup only — do not hold it across the
            // subsequent write/publish operations (Constitution D2).
            await using (var viewClient = await _pool.GetClientAsync(cancellationToken: ct))
            {
                var viewQuery = new QueryExpression("savedquery")
                {
                    ColumnSet = new ColumnSet("savedqueryid"),
                    TopCount = 1
                };
                viewQuery.Criteria.AddCondition("savedqueryid", ConditionOperator.Equal, request.DefaultViewId);
                var viewResult = await viewClient.RetrieveMultipleAsync(viewQuery, ct);
                if (viewResult.Entities.Count == 0)
                    throw new PpdsException(FormErrorCodes.ViewNotFound,
                        $"Saved query (view) with ID '{request.DefaultViewId}' not found.");
            }

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.AddSubgrid(doc, request);

            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Failed to add subgrid to form '{request.FormName}': {ex.Message}", ex);
        }
    }

    public async Task RemoveSubgridAsync(RemoveSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        try
        {
            var (entityLogicalName, formXmlStr, formId, _, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            var doc = XDocument.Parse(formXmlStr!);
            FormXmlEditor.RemoveSubgrid(doc, request.Label);

            await WriteFormXmlAsync(doc, formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Failed to remove subgrid '{request.Label}' from form '{request.FormName}': {ex.Message}", ex);
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private async Task<(string entityLogicalName, string? formXml, Guid formId, string resolvedName, int formType, bool isManaged, string? description)>
        FetchFormRecordAsync(string entityLogicalName, string formName, bool requireMainForm, CancellationToken ct, bool requireCustomizable = false, bool unpublished = true)
    {
        var query = new QueryExpression("systemform")
        {
            ColumnSet = new ColumnSet("formid", "name", "type", "formxml", "ismanaged", "iscustomizable", "description"),
            TopCount = 1
        };
        query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, entityLogicalName);
        if (Guid.TryParse(formName.Trim('{', '}'), out var formGuid))
            query.Criteria.AddCondition("formid", ConditionOperator.Equal, formGuid);
        else
            query.Criteria.AddCondition("name", ConditionOperator.Equal, formName);

        // systemform is a publishable entity. Read-modify-write mutations pass unpublished=true so
        // draft changes from a prior mutation (without --publish) are visible and compose instead of
        // being overwritten. Read-for-display (GetAsync) defaults to the published version unless the
        // caller opts into the draft via --unpublished.
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        EntityCollection result;
        if (unpublished)
        {
            var unpubRequest = new RetrieveUnpublishedMultipleRequest { Query = query };
            var unpubResponse = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(unpubRequest, ct);
            result = unpubResponse.EntityCollection;
        }
        else
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }

        if (result.Entities.Count == 0)
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Form '{formName}' not found for entity '{entityLogicalName}'.");

        var form = result.Entities[0];
        var formType = form.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0;

        if (requireMainForm && formType != MainFormType)
            throw new PpdsException(FormErrorCodes.FormNotFound,
                $"Form '{formName}' is not a Main form. Modification commands only support Main forms (type 2). Use `set-xml` to modify other form types.");

        // Dataverse silently ignores UpdateAsync on forms where iscustomizable=false — the call
        // succeeds with no error but the change does not persist. Detect this early so the user
        // gets a clear message instead of a silent no-op.
        if (requireCustomizable && form.GetAttributeValue<BooleanManagedProperty>("iscustomizable") is not { Value: true })
            throw new PpdsException(FormErrorCodes.FormNotCustomizable,
                $"Form '{formName}' on entity '{entityLogicalName}' is not customizable and cannot be modified by PPDS. " +
                "To make it customizable, add it to an unmanaged solution layer in this environment, then retry.");

        return (
            entityLogicalName,
            form.GetAttributeValue<string>("formxml"),
            form.GetAttributeValue<Guid>("formid"),
            form.GetAttributeValue<string>("name") ?? formName,
            formType,
            form.GetAttributeValue<bool>("ismanaged"),
            form.GetAttributeValue<string>("description")
        );
    }

    private async Task WriteFormXmlAsync(
        XDocument doc,
        Guid formId,
        string entityLogicalName,
        string? solutionUniqueName,
        bool publish,
        CancellationToken ct)
    {
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        FormXmlValidator.Validate(XDocument.Parse(xml));

        // D2: acquire, use, release — one client per operation
        await using (var client = await _pool.GetClientAsync(cancellationToken: ct))
        {
            var entity = new Entity("systemform", formId)
            {
                ["formxml"] = xml
            };
            await client.UpdateAsync(entity, ct);
        }

        await ApplySideEffectsAsync(formId, entityLogicalName, solutionUniqueName, publish, ct);
    }

    private async Task ApplySideEffectsAsync(
        Guid formId,
        string entityLogicalName,
        string? solutionUniqueName,
        bool publish,
        CancellationToken ct)
    {
        if (solutionUniqueName is not null)
        {
            var request = new AddSolutionComponentRequest
            {
                ComponentId = formId,
                ComponentType = SystemFormComponentType,
                SolutionUniqueName = solutionUniqueName,
                AddRequiredComponents = false
            };

            try
            {
                // D2: acquire, use, release — own client per operation
                await using var solutionClient = await _pool.GetClientAsync(cancellationToken: ct);
                await solutionClient.ExecuteAsync(request, ct);
            }
            catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == -2147159998)
            {
                // Component already in solution — treat as no-op (mirrors PluginRegistrationService.AddToSolutionAsync)
                _logger.LogDebug(
                    "Form {FormId} already exists in solution {SolutionName}, skipping",
                    formId, solutionUniqueName);
            }
        }

        if (publish)
        {
            // D2: acquire, use, release — own client per operation
            await using var publishClient = await _pool.GetClientAsync(cancellationToken: ct);
            var entityXml = $"<entity>{System.Security.SecurityElement.Escape(entityLogicalName)}</entity>";
            var parameterXml = $"<importexportxml><entities>{entityXml}</entities></importexportxml>";
            var environmentKey = publishClient.ConnectedOrgUniqueName ?? "default";
            await publishClient.PublishXmlAsync(parameterXml, environmentKey, ct);
        }
    }

    private static IReadOnlyList<TabDetail> ParseTabs(XDocument doc)
    {
        var result = new List<TabDetail>();
        foreach (var tab in doc.Descendants("tab"))
        {
            var idStr = (string?)tab.Attribute("id") ?? string.Empty;
            Guid.TryParse(idStr.Trim('{', '}'), out var tabId);
            var label = (string?)tab.Element("labels")?.Element("label")?.Attribute("description") ?? string.Empty;
            var expanded = (string?)tab.Attribute("expanded") == "1";
            var visible = (string?)tab.Attribute("visible") != "0";
            var columnCount = tab.Element("columns")?.Elements("column").Count() ?? 1;

            result.Add(new TabDetail(tabId, label, expanded, visible, columnCount, ParseSections(tab)));
        }

        return result;
    }

    private static IReadOnlyList<SectionDetail> ParseSections(XElement tab)
    {
        var result = new List<SectionDetail>();
        foreach (var section in tab.Descendants("section"))
        {
            var idStr = (string?)section.Attribute("id") ?? string.Empty;
            Guid.TryParse(idStr.Trim('{', '}'), out var sectionId);
            var label = (string?)section.Element("labels")?.Element("label")?.Attribute("description") ?? string.Empty;
            var columns = int.TryParse((string?)section.Attribute("columns"), out var c) ? c : 1;

            var fields = new List<FieldDetail>();
            var subgrids = new List<SubgridDetail>();
            var rowNum = 0;
            var colNum = 0;

            foreach (var cell in section.Descendants("cell"))
            {
                var control = cell.Element("control");
                if (control is null) continue;

                var classId = (string?)control.Attribute("classid");
                if (string.Equals(classId, ClassIdResolver.SubgridClassId, StringComparison.OrdinalIgnoreCase))
                {
                    var sgIdStr = (string?)control.Attribute("id") ?? string.Empty;
                    Guid.TryParse(sgIdStr.Trim('{', '}'), out var sgId);
                    var sgLabel = (string?)cell.Element("labels")?.Element("label")?.Attribute("description") ?? string.Empty;
                    var targetEntity = (string?)control.Element("parameters")?.Element("TargetEntityType") ?? string.Empty;
                    var viewIdStr = (string?)control.Element("parameters")?.Element("ViewId") ?? string.Empty;
                    Guid.TryParse(viewIdStr.Trim('{', '}'), out var viewId);
                    var relationship = (string?)control.Element("parameters")?.Element("RelationshipName");
                    subgrids.Add(new SubgridDetail(sgId, sgLabel, targetEntity, viewId, relationship));
                }
                else
                {
                    var fieldName = (string?)control.Attribute("datafieldname") ?? string.Empty;
                    var fieldLabel = (string?)cell.Element("labels")?.Element("label")?.Attribute("description");
                    fields.Add(new FieldDetail(fieldName, fieldLabel, colNum, rowNum));
                    colNum++;
                    if (colNum >= columns) { colNum = 0; rowNum++; }
                }
            }

            result.Add(new SectionDetail(sectionId, label, columns, fields, subgrids));
        }

        return result;
    }

    private static bool ClassIdCanBeResolved(string attributeType)
    {
        try { ClassIdResolver.ResolveForField(attributeType); return true; }
        catch { return false; }
    }

    private static string FormTypeName(int typeCode) => typeCode switch
    {
        1 => "Dashboard",
        2 => "Main",
        3 => "Mobile - Express",
        4 => "Preview",
        5 => "Mobile - Task",
        6 => "Quick View",
        7 => "Quick Create",
        8 => "Dialog",
        10 => "Power BI Dashboard",
        11 => "Card",
        12 => "Main - Interactive experience",
        100 => "Other",
        _ => $"Type {typeCode}"
    };
}
