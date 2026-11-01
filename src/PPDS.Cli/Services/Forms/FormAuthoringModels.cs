using System.Collections.Concurrent;

namespace PPDS.Cli.Services.Forms;

public sealed record AddFieldDraftRequest(
    string EntityLogicalName, Guid FormId, Guid SectionId, string FieldLogicalName,
    string ExpectedDraftHash, string? SolutionUniqueName = null, bool DryRun = false,
    string? ConfirmEnvironment = null);

public sealed record PublishFormEntityRequest(
    string EntityLogicalName, Guid FormId, string ExpectedDraftHash, string ConfirmPublishEntity,
    string? ConfirmEnvironment = null);

public sealed record FormPhaseResult(string Status = "not-attempted", string? ErrorCode = null, string? Message = null);

public sealed record FormAuthoringResult
{
    public required string EnvironmentUrl { get; init; }
    public required string EntityLogicalName { get; init; }
    public required Guid FormId { get; init; }
    public string Status { get; set; } = "planned";
    public bool Success => Status is "saved-unpublished" or "published" or "unchanged" or "planned";
    public bool DryRun { get; init; }
    public string ConcurrencyMode { get; set; } = "preflight-only";
    public string? DraftHash { get; set; }
    public Guid? SectionId { get; init; }
    public string? FieldLogicalName { get; init; }
    public string? SolutionUniqueName { get; init; }
    public string PublicationScope { get; init; } = "none";
    public string[] Warnings { get; set; } = [];
    public FormPhaseResult DraftWrite { get; set; } = new();
    public FormPhaseResult SolutionAssociation { get; set; } = new();
    public FormPhaseResult DraftVerification { get; set; } = new();
    public FormPhaseResult EntityPublication { get; set; } = new();
    public FormPhaseResult PublishedVerification { get; set; } = new();
}

/// <summary>Owns per-target form locks for a service session; MCP shares this across tool providers.</summary>
public sealed class FormMutationCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    public async Task<IDisposable> AcquireAsync(string environmentUrl, Guid formId, CancellationToken ct)
    {
        var key = Environment.EnvironmentConfirmationService.NormalizeUrl(environmentUrl) + "|" + formId;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }
    public void Dispose()
    {
        foreach (var gate in _locks.Values) gate.Dispose();
        _locks.Clear();
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}

