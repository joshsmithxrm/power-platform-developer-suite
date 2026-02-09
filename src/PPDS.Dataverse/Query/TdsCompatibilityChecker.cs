using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Determines whether a SQL query can be executed via the Dataverse TDS Endpoint.
/// The TDS Endpoint is read-only and does not support all entity types or SQL features.
/// </summary>
public static class TdsCompatibilityChecker
{
    /// <summary>
    /// SQL keywords that indicate DML (data manipulation) statements.
    /// The TDS Endpoint only supports SELECT queries.
    /// </summary>
    private static readonly HashSet<string> DmlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "CREATE", "ALTER"
    };

    /// <summary>
    /// Entity types that are not supported by the TDS Endpoint.
    /// Elastic tables, virtual entities, and activity party are not available via TDS.
    /// </summary>
    private static readonly HashSet<string> IncompatibleEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        "msdyn_aborecord",         // Elastic table (NoSQL)
        "msdyn_aaborecord",        // Elastic table (NoSQL)
        "activityparty",           // Activity party (virtual)
    };

    /// <summary>
    /// Prefixes for entity logical names that indicate virtual/elastic tables.
    /// </summary>
    private static readonly string[] IncompatibleEntityPrefixes =
    [
        "virtual_",
    ];

    /// <summary>
    /// Checks whether a SQL query is compatible with the TDS Endpoint.
    /// </summary>
    /// <param name="sql">The SQL query to check.</param>
    /// <param name="entityLogicalName">The primary entity logical name, if known.</param>
    /// <returns>The compatibility result.</returns>
    public static TdsCompatibility CheckCompatibility(string sql, string? entityLogicalName = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return TdsCompatibility.IncompatibleFeature;
        }

        // Check for DML statements
        if (IsDmlStatement(sql))
        {
            return TdsCompatibility.IncompatibleDml;
        }

        // Check entity compatibility
        if (!string.IsNullOrEmpty(entityLogicalName) && IsIncompatibleEntity(entityLogicalName))
        {
            return TdsCompatibility.IncompatibleEntity;
        }

        return TdsCompatibility.Compatible;
    }

    /// <summary>
    /// Checks whether the SQL statement is a DML statement (non-SELECT).
    /// </summary>
    /// <param name="sql">The SQL statement to check.</param>
    /// <returns>True if the statement is DML and cannot be executed via TDS.</returns>
    public static bool IsDmlStatement(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var trimmed = sql.TrimStart();

        // Check if the first keyword is a DML keyword
        foreach (var keyword in DmlKeywords)
        {
            if (trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                // Ensure it's a full keyword (followed by whitespace or end of string)
                if (trimmed.Length == keyword.Length ||
                    char.IsWhiteSpace(trimmed[keyword.Length]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an entity is incompatible with the TDS Endpoint.
    /// </summary>
    /// <param name="entityLogicalName">The logical name of the entity.</param>
    /// <returns>True if the entity cannot be queried via TDS.</returns>
    public static bool IsIncompatibleEntity(string entityLogicalName)
    {
        if (string.IsNullOrEmpty(entityLogicalName))
        {
            return false;
        }

        if (IncompatibleEntities.Contains(entityLogicalName))
        {
            return true;
        }

        foreach (var prefix in IncompatibleEntityPrefixes)
        {
            if (entityLogicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Result of a TDS Endpoint compatibility check.
/// </summary>
public enum TdsCompatibility
{
    /// <summary>The query can be executed via the TDS Endpoint.</summary>
    Compatible,

    /// <summary>The target entity is not available via the TDS Endpoint (e.g., elastic/virtual table).</summary>
    IncompatibleEntity,

    /// <summary>The query uses a feature not supported by the TDS Endpoint (e.g., virtual *name columns).</summary>
    IncompatibleFeature,

    /// <summary>The query is a DML statement (INSERT, UPDATE, DELETE) which TDS does not support.</summary>
    IncompatibleDml
}
