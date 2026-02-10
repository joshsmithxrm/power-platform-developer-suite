using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Plan node for EXECUTE AS USER = 'user@domain.com'.
/// Sets the CallerObjectId on the SessionContext for subsequent Dataverse requests.
/// </summary>
/// <remarks>
/// ScriptDom parses this as an ExecuteAsStatement.
/// The CallerObjectId is resolved from the user's domain name. For the initial
/// implementation, the user principal name is stored and the Guid resolution
/// is deferred to the execution layer.
/// </remarks>
public sealed class ExecuteAsNode : IQueryPlanNode
{
    /// <summary>The user principal name to impersonate (e.g., "user@domain.com").</summary>
    public string UserPrincipalName { get; }

    /// <summary>Optional pre-resolved CallerObjectId (Guid) for the user.</summary>
    public Guid? CallerObjectId { get; }

    /// <summary>The session context to set impersonation on.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"ExecuteAs: {UserPrincipalName}";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecuteAsNode"/> class.
    /// </summary>
    /// <param name="userPrincipalName">The user principal name to impersonate.</param>
    /// <param name="session">The session context.</param>
    /// <param name="callerObjectId">Optional pre-resolved CallerObjectId.</param>
    public ExecuteAsNode(string userPrincipalName, SessionContext session, Guid? callerObjectId = null)
    {
        UserPrincipalName = userPrincipalName ?? throw new ArgumentNullException(nameof(userPrincipalName));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        CallerObjectId = callerObjectId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // If we have a pre-resolved Guid, use it directly.
        // Otherwise, store a deterministic Guid derived from the UPN for now.
        // In a future phase, this will resolve the actual systemuserid from Dataverse.
        if (CallerObjectId.HasValue)
        {
            Session.CallerObjectId = CallerObjectId.Value;
        }
        else
        {
            // TODO: Resolve the user's systemuserid from Dataverse using the UPN.
            // For now, generate a deterministic Guid from the UPN for testing purposes.
            Session.CallerObjectId = GenerateDeterministicGuid(UserPrincipalName);
        }

        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Generates a deterministic Guid from a string (for testing/placeholder purposes).
    /// </summary>
    private static Guid GenerateDeterministicGuid(string input)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input.ToLowerInvariant());
        var hash = md5.ComputeHash(bytes);
        return new Guid(hash);
    }
}

/// <summary>
/// Plan node for REVERT.
/// Clears impersonation from the SessionContext.
/// </summary>
/// <remarks>
/// ScriptDom parses this as a RevertStatement.
/// </remarks>
public sealed class RevertNode : IQueryPlanNode
{
    /// <summary>The session context to clear impersonation from.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => "Revert: clear impersonation";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="RevertNode"/> class.
    /// </summary>
    /// <param name="session">The session context.</param>
    public RevertNode(SessionContext session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Session.CallerObjectId = null;
        await Task.CompletedTask;
        yield break;
    }
}
