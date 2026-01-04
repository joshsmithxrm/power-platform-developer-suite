namespace PPDS.Dataverse.Query;

/// <summary>
/// Represents a column in a query result with its metadata.
/// </summary>
public sealed class QueryColumn
{
    /// <summary>
    /// The logical name of the attribute.
    /// </summary>
    public required string LogicalName { get; init; }

    /// <summary>
    /// The alias assigned to this column in the query, if any.
    /// Used for aggregate functions and renamed columns.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// The display name of the attribute from metadata, if available.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The data type of this column's values.
    /// </summary>
    public QueryColumnType DataType { get; init; } = QueryColumnType.Unknown;

    /// <summary>
    /// For link-entity columns, the alias of the linked entity.
    /// </summary>
    public string? LinkedEntityAlias { get; init; }

    /// <summary>
    /// For link-entity columns, the logical name of the linked entity.
    /// </summary>
    public string? LinkedEntityName { get; init; }

    /// <summary>
    /// Whether this column is from an aggregate function.
    /// </summary>
    public bool IsAggregate { get; init; }

    /// <summary>
    /// The aggregate function used, if this is an aggregate column.
    /// </summary>
    public string? AggregateFunction { get; init; }

    /// <summary>
    /// Gets the effective name to use for this column in results.
    /// Returns the alias if set, otherwise the logical name.
    /// </summary>
    public string EffectiveName => Alias ?? LogicalName;

    /// <summary>
    /// Gets the fully qualified name including linked entity alias if present.
    /// </summary>
    public string QualifiedName =>
        LinkedEntityAlias != null
            ? $"{LinkedEntityAlias}.{LogicalName}"
            : LogicalName;
}
