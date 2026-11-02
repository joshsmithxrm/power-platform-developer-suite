using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Nonsecret dispatch/acknowledgement evidence; never infers rollback or server persistence.</summary>
internal sealed class PluginMutationJournal(string scope = "mutation")
{
    private const string ExceptionKey = "PPDS.PluginMutationOutcomes";
    private readonly List<PluginMutationOutcome> _outcomes = [];
    internal IReadOnlyList<PluginMutationOutcome> Outcomes => _outcomes;

    internal async Task<Guid> RunAsync(string entity, string operation, string phase, Guid? id,
        Func<Task<Guid>> action, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = _outcomes.Count;
            _outcomes.Add(new(Guid.NewGuid(), scope, entity, operation, phase, id, "unknown"));
            var returnedId = await action();
            _outcomes[index] = _outcomes[index] with { RecordId = returnedId, Outcome = "acknowledged" };
            return returnedId;
        }
        catch (Exception ex)
        {
            Attach(ex);
            throw; // Preserve the exact exception, including OperationCanceledException and stack.
        }
    }

    internal async Task RunAsync(string entity, string operation, string phase, Guid? id,
        Func<Task> action, CancellationToken cancellationToken)
    {
        await RunAsync(entity, operation, phase, id, async () =>
        {
            await action();
            return id ?? Guid.Empty;
        }, cancellationToken);
    }

    internal void Attach(Exception exception)
    {
        var combined = Read(exception).Concat(_outcomes).DistinctBy(o => o.EventId).ToArray();
        exception.Data[ExceptionKey] = combined;
    }

    internal static IReadOnlyList<PluginMutationOutcome> Read(Exception exception) =>
        exception.Data[ExceptionKey] as PluginMutationOutcome[] ?? [];
}

internal sealed record PluginMutationOutcome(
    [property: JsonIgnore] Guid EventId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("recordId")] Guid? RecordId,
    [property: JsonPropertyName("outcome")] string Outcome);
