namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Machine-readable error codes for metadata authoring validation failures.
/// </summary>
public static class MetadataErrorCodes
{
    /// <summary>Schema name is invalid (must start with letter, alphanumeric + underscore only).</summary>
    public const string InvalidSchemaName = "INVALID_SCHEMA_NAME";

    /// <summary>Schema name does not start with the expected publisher prefix.</summary>
    public const string InvalidPrefix = "INVALID_PREFIX";

    /// <summary>A required field is missing or empty.</summary>
    public const string MissingRequiredField = "MISSING_REQUIRED_FIELD";

    /// <summary>A numeric or length constraint is invalid (e.g., MaxLength &lt; 1).</summary>
    public const string InvalidConstraint = "INVALID_CONSTRAINT";

    /// <summary>Key attribute count is outside the allowed range (1-16).</summary>
    public const string InvalidKeyAttributeCount = "INVALID_KEY_ATTRIBUTE_COUNT";

    /// <summary>Duplicate option values detected within a single option set.</summary>
    public const string DuplicateOptionValue = "DUPLICATE_OPTION_VALUE";

    /// <summary>The referenced entity was not found.</summary>
    public const string EntityNotFound = "ENTITY_NOT_FOUND";

    /// <summary>Lookup columns must be created via a relationship, not directly.</summary>
    public const string UseRelationshipForLookup = "USE_RELATIONSHIP_FOR_LOOKUP";

    /// <summary>The maximum number of alternate keys per entity has been reached.</summary>
    public const string KeyLimitExceeded = "KEY_LIMIT_EXCEEDED";

    /// <summary>One or more key attributes are invalid for alternate key use.</summary>
    public const string InvalidKeyAttribute = "INVALID_KEY_ATTRIBUTE";

    /// <summary>A component with the same schema name already exists.</summary>
    public const string DuplicateSchemaName = "DUPLICATE_SCHEMA_NAME";

    /// <summary>Cannot delete a managed component.</summary>
    public const string CannotDeleteManaged = "CANNOT_DELETE_MANAGED";

    /// <summary>Deletion is blocked by existing dependencies.</summary>
    public const string DependencyConflict = "DEPENDENCY_CONFLICT";
}
