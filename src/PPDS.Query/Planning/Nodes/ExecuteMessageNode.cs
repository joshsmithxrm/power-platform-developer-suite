using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Plan node for EXEC message_name @param1 = value1, @param2 = value2.
/// Represents execution of a Dataverse message (Organization Request).
/// </summary>
/// <remarks>
/// ScriptDom parses EXEC statements as ExecuteStatement with ExecuteSpecification.
/// This node captures the message name and parameters, with the actual Dataverse
/// message execution to be wired in a future phase via the QueryPlanContext.
/// </remarks>
public sealed class ExecuteMessageNode : IQueryPlanNode
{
    /// <summary>The Dataverse message name (e.g., "WhoAmI", "SetState", "QualifyLead").</summary>
    public string MessageName { get; }

    /// <summary>Named parameters for the message, in declaration order.</summary>
    public IReadOnlyList<MessageParameter> Parameters { get; }

    /// <inheritdoc />
    public string Description => Parameters.Count > 0
        ? $"ExecuteMessage: {MessageName} ({Parameters.Count} params)"
        : $"ExecuteMessage: {MessageName}";

    /// <inheritdoc />
    public long EstimatedRows => 1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecuteMessageNode"/> class.
    /// </summary>
    /// <param name="messageName">The Dataverse message name.</param>
    /// <param name="parameters">The message parameters.</param>
    public ExecuteMessageNode(string messageName, IReadOnlyList<MessageParameter> parameters)
    {
        MessageName = messageName ?? throw new ArgumentNullException(nameof(messageName));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        await Task.CompletedTask;
        throw new NotSupportedException(
            $"EXECUTE '{MessageName}' is not yet supported. " +
            "Dataverse message execution will be available in a future release.");
    }
}

/// <summary>
/// A named parameter for a Dataverse message execution.
/// </summary>
public sealed class MessageParameter
{
    /// <summary>The parameter name (without @ prefix).</summary>
    public string Name { get; }

    /// <summary>The parameter value (as a string or typed object).</summary>
    public object? Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageParameter"/> class.
    /// </summary>
    public MessageParameter(string name, object? value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value;
    }
}
