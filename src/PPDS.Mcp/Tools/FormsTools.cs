using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>Bounded form authoring through the shared application service.</summary>
[McpServerToolType]
public sealed class FormsTools(McpToolContext context) : McpToolBase(context)
{
    [McpServerTool(Name = "ppds_forms_list", ReadOnly = true)]
    [Description("List forms for a Dataverse table, including stable IDs, form types and managed status.")]
    public Task<FormListResult> ListAsync(
        [Description("Table logical name")] string entityLogicalName,
        CancellationToken cancellationToken = default)
        => InvokeAsync(async provider =>
        {
            var result = await provider.GetRequiredService<IFormService>().ListAsync(entityLogicalName, cancellationToken).ConfigureAwait(false);
            var target = provider.GetRequiredService<IEnvironmentConfirmationService>().Context.EnvironmentUrl;
            return new FormListResult(target, entityLogicalName, result.Items, result.TotalCount);
        }, cancellationToken, (nameof(entityLogicalName), entityLogicalName));

    [McpServerTool(Name = "ppds_forms_get", ReadOnly = true)]
    [Description("Inspect a form by ID. Use unpublished=true to inspect its draft and obtain the fingerprint required before editing or publishing.")]
    public Task<FormGetResult> GetAsync(
        [Description("Table logical name")] string entityLogicalName,
        [Description("Stable form ID")] Guid formId,
        [Description("Read unpublished draft instead of published form")] bool unpublished = false,
        CancellationToken cancellationToken = default)
    {
        if (formId == Guid.Empty) throw new ArgumentException("A stable form ID is required.", nameof(formId));
        return InvokeAsync(async provider =>
        {
            var detail = await provider.GetRequiredService<IFormService>()
                .GetAsync(entityLogicalName, formId.ToString(), unpublished, cancellationToken).ConfigureAwait(false);
            return new FormGetResult(provider.GetRequiredService<IEnvironmentConfirmationService>().Context.EnvironmentUrl,
                entityLogicalName, detail);
        }, cancellationToken, (nameof(entityLogicalName), entityLogicalName));
    }

    [McpServerTool(Name = "ppds_forms_add_field", ReadOnly = false, Destructive = false)]
    [Description("Add one existing column to an existing Main-form section by stable IDs. Saves and verifies an unpublished draft; never publishes. Use dryRun=true for a write-free preview. Avoid simultaneous editors when concurrencyMode is preflight-only.")]
    public Task<FormAuthoringResult> AddFieldAsync(
        string entityLogicalName, Guid formId, Guid sectionId, string fieldLogicalName,
        [Description("Fingerprint from a fresh unpublished forms get")] string expectedDraftHash,
        string? solutionUniqueName = null, bool dryRun = false,
        [Description("Exact environment URL acknowledgement for the first protected mutation in this session")] string? confirmEnvironment = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(provider => provider.GetRequiredService<IFormService>().AddFieldDraftAsync(
            new AddFieldDraftRequest(entityLogicalName, formId, sectionId, fieldLogicalName, expectedDraftHash,
                solutionUniqueName, dryRun, confirmEnvironment),
            new McpFormProgressReporter(progress), cancellationToken),
            cancellationToken, (nameof(entityLogicalName), entityLogicalName),
            (nameof(fieldLogicalName), fieldLogicalName), (nameof(expectedDraftHash), expectedDraftHash));

    [McpServerTool(Name = "ppds_forms_publish", ReadOnly = false)]
    [Description("Publish ALL pending customizations for the selected table, including changes beyond the selected form. Requires confirmPublishEntity equal to the table logical name. Verifies only the selected form after entity-scoped publication.")]
    public Task<FormAuthoringResult> PublishAsync(
        string entityLogicalName, Guid formId, string expectedDraftHash,
        [Description("Exact table logical name acknowledging table-wide publication")] string confirmPublishEntity,
        string? confirmEnvironment = null, IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(provider => provider.GetRequiredService<IFormService>().PublishFormEntityAsync(
            new PublishFormEntityRequest(entityLogicalName, formId, expectedDraftHash, confirmPublishEntity, confirmEnvironment),
            new McpFormProgressReporter(progress), cancellationToken),
            cancellationToken, (nameof(entityLogicalName), entityLogicalName),
            (nameof(expectedDraftHash), expectedDraftHash), (nameof(confirmPublishEntity), confirmPublishEntity));

    private async Task<T> InvokeAsync<T>(
        Func<ServiceProvider, Task<T>> action, CancellationToken ct, params (string name, object? value)[] required)
    {
        try
        {
            await using var provider = await CreateScopeAsync(ct, required).ConfigureAwait(false);
            return await action(provider).ConfigureAwait(false);
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw;
        }
    }

    private sealed class McpFormProgressReporter(IProgress<ProgressNotificationValue>? progress) : IProgressReporter
    {
        private int _step;
        private void Report(string message) => progress?.Report(new ProgressNotificationValue
            { Progress = Interlocked.Increment(ref _step), Message = message });
        public void ReportProgress(ProgressSnapshot snapshot) => Report(snapshot.StatusMessage ?? $"{snapshot.CurrentItem} of {snapshot.TotalItems}");
        public void ReportPhase(string phase, string? detail = null) => Report(detail is null ? phase : $"{phase}: {detail}");
        public void ReportWarning(string message) => Report(message);
        public void ReportInfo(string message) => Report(message);
    }
}

public sealed record FormListResult(string EnvironmentUrl, string EntityLogicalName, IReadOnlyList<FormInfo> Forms, int TotalCount);
public sealed record FormGetResult(string EnvironmentUrl, string EntityLogicalName, FormDetail? Form);

