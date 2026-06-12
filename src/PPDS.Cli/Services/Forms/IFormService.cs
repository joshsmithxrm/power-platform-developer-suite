using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.Models;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Domain service for Dataverse systemform operations.
/// Single code path for CLI, TUI, Extension, and MCP (Constitution A1, A2).
/// </summary>
public interface IFormService
{
    // ── Read ──────────────────────────────────────────────────────────────

    Task<ListResult<FormInfo>> ListAsync(string entityLogicalName, CancellationToken ct = default);

    Task<FormDetail?> GetAsync(string entityLogicalName, string formName, bool unpublished = false, CancellationToken ct = default);

    // ── Set XML ───────────────────────────────────────────────────────────

    Task SetFormXmlAsync(SetFormXmlRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // ── Tab management ────────────────────────────────────────────────────

    Task<TabResult> AddTabAsync(AddTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task UpdateTabAsync(UpdateTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task RemoveTabAsync(RemoveTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task<TabInfo?> FindTabAsync(FindTabRequest request, CancellationToken ct = default);

    // ── Section management ────────────────────────────────────────────────

    Task<SectionResult> AddSectionAsync(AddSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task UpdateSectionAsync(UpdateSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task RemoveSectionAsync(RemoveSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task<SectionInfo?> FindSectionAsync(FindSectionRequest request, CancellationToken ct = default);

    // ── Field management ──────────────────────────────────────────────────

    Task AddFieldAsync(AddFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task RemoveFieldAsync(RemoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task ReorderFieldsAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // ── Sub-grid management ───────────────────────────────────────────────

    Task AddSubgridAsync(AddSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    Task RemoveSubgridAsync(RemoveSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record FormInfo(
    Guid Id,
    string Name,
    int FormType,
    string FormTypeName,
    bool IsManaged,
    string? Description);

public sealed record FormDetail(
    Guid Id,
    string Name,
    int FormType,
    string FormTypeName,
    bool IsManaged,
    string? Description,
    IReadOnlyList<TabDetail> Tabs,
    string? FormXml = null);

public sealed record TabDetail(
    Guid Id,
    string Name,
    string Label,
    bool Expanded,
    bool Visible,
    int Columns,
    IReadOnlyList<SectionDetail> Sections);

public sealed record SectionDetail(
    Guid Id,
    string Name,
    string Label,
    int Columns,
    IReadOnlyList<FieldDetail> Fields,
    IReadOnlyList<SubgridDetail> Subgrids);

public sealed record FieldDetail(
    string LogicalName,
    string? Label,
    int ColumnNumber,
    int RowNumber);

public sealed record SubgridDetail(
    string Name,
    string Label,
    string TargetEntity,
    Guid ViewId,
    string? Relationship);

public sealed record TabResult(Guid TabId, string TabLabel);

public sealed record TabInfo(Guid TabId, string TabLabel, int Position);

public sealed record SectionResult(Guid SectionId, string SectionLabel, string TabLabel);

public sealed record SectionInfo(Guid SectionId, string SectionLabel, string TabLabel, Guid TabId);

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record SetFormXmlRequest(
    string EntityLogicalName,
    string FormName,
    string FormXml,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record AddTabRequest(
    string EntityLogicalName,
    string FormName,
    string Label,
    bool ShowLabel = true,
    bool Expanded = true,
    bool Visible = true,
    bool AvailableOnPhone = true,
    int Columns = 1,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record UpdateTabRequest(
    string EntityLogicalName,
    string FormName,
    string TabLabel,
    string? NewLabel = null,
    bool? ShowLabel = null,
    bool? Expanded = null,
    bool? Visible = null,
    bool? AvailableOnPhone = null,
    int? Columns = null,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record RemoveTabRequest(
    string EntityLogicalName,
    string FormName,
    string TabLabel,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record FindTabRequest(
    string EntityLogicalName,
    string FormName,
    string TabLabel);

// Note: sections have no "expanded" attribute in the Dataverse form schema
// (only tabs do — see FormXml.xsd). The issue's section property list mirrored
// the tab list; the schema is authoritative, so section expand state is omitted.
public sealed record AddSectionRequest(
    string EntityLogicalName,
    string FormName,
    string TabLabel,
    string Label,
    bool ShowLabel = true,
    int Columns = 1,
    bool Visible = true,
    bool AvailableOnPhone = true,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record UpdateSectionRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel,
    string? NewLabel = null,
    bool? ShowLabel = null,
    int? Columns = null,
    bool? Visible = null,
    bool? AvailableOnPhone = null,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record RemoveSectionRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record FindSectionRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel);

public sealed record AddFieldRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel,
    string[] FieldLogicalNames,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record RemoveFieldRequest(
    string EntityLogicalName,
    string FormName,
    string FieldLogicalName,
    string? SectionLabelOrId = null,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record ReorderFieldsRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel,
    string[] FieldLogicalNames,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record AddSubgridRequest(
    string EntityLogicalName,
    string FormName,
    string SectionLabel,
    string Label,
    string TargetEntity,
    Guid DefaultViewId,
    string? Relationship = null,
    bool HideLabel = false,
    bool HideOnPhone = false,
    int MaxRows = 5,
    bool HideSearchBox = false,
    string? SolutionUniqueName = null,
    bool Publish = false);

public sealed record RemoveSubgridRequest(
    string EntityLogicalName,
    string FormName,
    string Label,
    string? SolutionUniqueName = null,
    bool Publish = false);
