using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.PluginTraces;

/// <summary>What a <c>--older-than</c> clean-up was asked to do.</summary>
/// <param name="Age">The age, already parsed (<see cref="PluginTraceAge.Parse"/>).</param>
/// <param name="DryRun">Count only; delete nothing.</param>
/// <param name="Force">Delete without asking.</param>
/// <param name="JsonOutput">The output is JSON, so no prompt can be shown.</param>
/// <param name="InputRedirected">Standard input is redirected, so no prompt can be answered.</param>
internal sealed record OlderThanDeletionRequest(TimeSpan Age, bool DryRun, bool Force, bool JsonOutput, bool InputRedirected);

/// <summary>How a <c>--older-than</c> clean-up ended.</summary>
internal enum OlderThanDeletionStatus
{
    /// <summary>A dry run: counted, nothing deleted.</summary>
    Previewed,

    /// <summary>No trace was older than the age; nothing deleted.</summary>
    NothingToDelete,

    /// <summary>The prompt was declined; nothing deleted.</summary>
    Declined,

    /// <summary>The traces created before the cut-off were deleted.</summary>
    Deleted,
}

/// <summary>The outcome of a <c>--older-than</c> clean-up.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="CutoffUtc">The one cut-off used for the count, the prompt and the delete (UTC).</param>
/// <param name="Count">The traces created before the cut-off, as counted first.</param>
/// <param name="Deleted">The traces deleted (0 unless <see cref="OlderThanDeletionStatus.Deleted"/>).</param>
internal sealed record OlderThanDeletionResult(OlderThanDeletionStatus Status, DateTime CutoffUtc, int Count, int Deleted);

/// <summary>
/// The <c>plugintraces delete --older-than</c> clean-up: one cut-off from the clock in UTC, a count,
/// a confirmation unless forced, then a delete of exactly the traces created before that same cut-off.
/// The CLI renders it; the decisions live here so they are tested without a connection.
/// </summary>
internal static class PluginTraceOlderThanDeletion
{
    /// <summary>
    /// Why a run cannot go ahead, or null: without <c>--force</c> or <c>--dry-run</c> it needs the
    /// confirmation prompt, which JSON output and redirected input cannot give.
    /// </summary>
    /// <param name="force">Delete without asking.</param>
    /// <param name="dryRun">Count only.</param>
    /// <param name="jsonOutput">The output is JSON.</param>
    /// <param name="inputRedirected">Standard input is redirected.</param>
    /// <returns>The reason, or null when the run can go ahead.</returns>
    internal static string? ConfirmationProblem(bool force, bool dryRun, bool jsonOutput, bool inputRedirected)
    {
        if (force || dryRun || (!jsonOutput && !inputRedirected))
        {
            return null;
        }

        return "--older-than asks to confirm the cut-off and the count before deleting, which is not possible " +
               "with JSON output or redirected input. Add --force to delete without asking, or --dry-run to see the count.";
    }

    /// <summary>
    /// Runs the clean-up.
    /// </summary>
    /// <param name="traces">The trace service.</param>
    /// <param name="clock">The clock the cut-off is computed from, read once, in UTC.</param>
    /// <param name="request">What was asked.</param>
    /// <param name="confirm">Asked with the cut-off and the count when not forced; true to delete.</param>
    /// <param name="deleting">Told the cut-off and the count just before the delete starts.</param>
    /// <param name="progress">Progress of the delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> when the run cannot be confirmed
    /// (<see cref="ConfirmationProblem"/>) or the age is not valid; nothing is read.
    /// </exception>
    internal static async Task<OlderThanDeletionResult> RunAsync(
        IPluginTraceService traces,
        TimeProvider clock,
        OlderThanDeletionRequest request,
        Func<DateTime, int, bool> confirm,
        Action<DateTime, int>? deleting = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirm);

        var problem = ConfirmationProblem(request.Force, request.DryRun, request.JsonOutput, request.InputRedirected);
        if (problem != null)
        {
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, problem);
        }

        // The cut-off is computed once, in UTC; the count, the prompt and the delete all use it.
        var cutoff = PluginTraceAge.CutoffUtc(request.Age, clock.GetUtcNow().UtcDateTime);
        var count = await traces.CountAsync(new PluginTraceFilter { CreatedBefore = cutoff }, cancellationToken)
            .ConfigureAwait(false);

        if (request.DryRun)
        {
            return new OlderThanDeletionResult(OlderThanDeletionStatus.Previewed, cutoff, count, 0);
        }

        if (count == 0)
        {
            return new OlderThanDeletionResult(OlderThanDeletionStatus.NothingToDelete, cutoff, 0, 0);
        }

        if (!request.Force && !confirm(cutoff, count))
        {
            return new OlderThanDeletionResult(OlderThanDeletionStatus.Declined, cutoff, count, 0);
        }

        deleting?.Invoke(cutoff, count);
        var deleted = await traces.DeleteCreatedBeforeAsync(cutoff, progress, cancellationToken).ConfigureAwait(false);
        return new OlderThanDeletionResult(OlderThanDeletionStatus.Deleted, cutoff, count, deleted);
    }
}
