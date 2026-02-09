using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// A single row flowing through the plan. Lightweight wrapper around column values.
/// </summary>
public sealed class QueryRow
{
    /// <summary>Column name → value.</summary>
    public IReadOnlyDictionary<string, QueryValue> Values { get; }

    /// <summary>The entity logical name this row originated from.</summary>
    public string EntityLogicalName { get; }

    /// <summary>Initializes a new instance of the <see cref="QueryRow"/> class.</summary>
    public QueryRow(IReadOnlyDictionary<string, QueryValue> values, string entityLogicalName)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
    }

    /// <summary>
    /// Creates a QueryRow from a QueryResult record.
    /// </summary>
    public static QueryRow FromRecord(IReadOnlyDictionary<string, QueryValue> record, string entityLogicalName)
    {
        return new QueryRow(record, entityLogicalName);
    }
}
