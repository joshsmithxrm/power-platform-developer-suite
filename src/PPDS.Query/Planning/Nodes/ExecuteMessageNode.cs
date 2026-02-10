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
        // TODO: Wire to Dataverse message execution via QueryPlanContext.
        // For now, return a single row with the message name and parameters as confirmation.
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = QueryValue.Simple(MessageName),
            ["status"] = QueryValue.Simple("pending"),
            ["parameter_count"] = QueryValue.Simple(Parameters.Count)
        };

        // Include each parameter as a column for visibility
        foreach (var param in Parameters)
        {
            values[param.Name] = QueryValue.Simple(param.Value);
        }

        yield return new QueryRow(values, "message");
        context.Statistics.IncrementRowsRead();
        await Task.CompletedTask;
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
