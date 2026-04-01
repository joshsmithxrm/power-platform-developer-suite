using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Validates metadata authoring requests before SDK calls.
/// Pure logic, no Dataverse dependencies.
/// </summary>
public sealed class SchemaValidator
{
    private static readonly Regex SchemaNameRegex = new(@"^[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Validates that a schema name follows Dataverse naming rules.
    /// Must start with a letter and contain only alphanumeric characters and underscores.
    /// </summary>
    public void ValidateSchemaName(string schemaName, string fieldName = "SchemaName")
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.MissingRequiredField,
                $"{fieldName} is required.",
                fieldName);
        }

        if (!SchemaNameRegex.IsMatch(schemaName))
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.InvalidSchemaName,
                $"Schema name '{schemaName}' is invalid. Must start with a letter and contain only alphanumeric characters and underscores.",
                fieldName);
        }
    }

    /// <summary>
    /// Validates that a schema name starts with the expected publisher prefix.
    /// </summary>
    public void ValidatePrefix(string schemaName, string expectedPrefix, string fieldName = "SchemaName")
    {
        var prefixWithUnderscore = expectedPrefix + "_";
        if (!schemaName.StartsWith(prefixWithUnderscore, StringComparison.OrdinalIgnoreCase))
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.InvalidPrefix,
                $"Schema name '{schemaName}' must start with publisher prefix '{prefixWithUnderscore}'.",
                fieldName);
        }
    }

    /// <summary>
    /// Validates that a required string value is non-null and non-empty.
    /// </summary>
    public void ValidateRequiredString(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.MissingRequiredField,
                $"{fieldName} is required.",
                fieldName);
        }
    }

    /// <summary>
    /// Validates a <see cref="CreateTableRequest"/>.
    /// </summary>
    public void ValidateCreateTableRequest(CreateTableRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.DisplayName, "DisplayName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.PluralDisplayName, "PluralDisplayName"));

        if (!string.IsNullOrEmpty(request.OwnershipType) &&
            !request.OwnershipType.Equals("UserOwned", StringComparison.OrdinalIgnoreCase) &&
            !request.OwnershipType.Equals("OrganizationOwned", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new ValidationMessage
            {
                Field = "OwnershipType",
                Rule = MetadataErrorCodes.InvalidConstraint,
                Message = "OwnershipType must be 'UserOwned' or 'OrganizationOwned'."
            });
        }

        ThrowIfErrors(messages);
    }

    /// <summary>
    /// Validates a <see cref="CreateColumnRequest"/>. Rejects Lookup type.
    /// </summary>
    public void ValidateCreateColumnRequest(CreateColumnRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.DisplayName, "DisplayName"));

        if (request.ColumnType == SchemaColumnType.Lookup)
        {
            messages.Add(new ValidationMessage
            {
                Field = "ColumnType",
                Rule = MetadataErrorCodes.UseRelationshipForLookup,
                Message = "Lookup columns must be created via CreateOneToManyAsync, not CreateColumnAsync."
            });
        }

        if (request.ColumnType == SchemaColumnType.String && request.MaxLength.HasValue && request.MaxLength.Value < 1)
        {
            messages.Add(new ValidationMessage
            {
                Field = "MaxLength",
                Rule = MetadataErrorCodes.InvalidConstraint,
                Message = "MaxLength must be at least 1."
            });
        }

        if (request.ColumnType == SchemaColumnType.Memo && request.MaxLength.HasValue && request.MaxLength.Value < 1)
        {
            messages.Add(new ValidationMessage
            {
                Field = "MaxLength",
                Rule = MetadataErrorCodes.InvalidConstraint,
                Message = "MaxLength must be at least 1."
            });
        }

        if ((request.ColumnType == SchemaColumnType.Integer || request.ColumnType == SchemaColumnType.Decimal ||
             request.ColumnType == SchemaColumnType.Double || request.ColumnType == SchemaColumnType.Money) &&
            request.MinValue.HasValue && request.MaxValue.HasValue && request.MinValue.Value > request.MaxValue.Value)
        {
            messages.Add(new ValidationMessage
            {
                Field = "MinValue",
                Rule = MetadataErrorCodes.InvalidConstraint,
                Message = "MinValue cannot be greater than MaxValue."
            });
        }

        if ((request.ColumnType == SchemaColumnType.Choice || request.ColumnType == SchemaColumnType.Choices) &&
            request.Options != null)
        {
            var duplicateValues = request.Options
                .GroupBy(o => o.Value)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateValues.Count > 0)
            {
                messages.Add(new ValidationMessage
                {
                    Field = "Options",
                    Rule = MetadataErrorCodes.DuplicateOptionValue,
                    Message = $"Duplicate option values: {string.Join(", ", duplicateValues)}."
                });
            }
        }

        ThrowIfErrors(messages);
    }

    /// <summary>
    /// Validates a <see cref="CreateOneToManyRequest"/>.
    /// </summary>
    public void ValidateCreateOneToManyRequest(CreateOneToManyRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.ReferencedEntity, "ReferencedEntity"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.ReferencingEntity, "ReferencingEntity"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.LookupSchemaName, "LookupSchemaName"));
        CollectIfInvalid(messages, () => ValidatePrefix(request.LookupSchemaName, publisherPrefix, "LookupSchemaName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.LookupDisplayName, "LookupDisplayName"));

        ThrowIfErrors(messages);
    }

    /// <summary>
    /// Validates a <see cref="CreateManyToManyRequest"/>.
    /// </summary>
    public void ValidateCreateManyToManyRequest(CreateManyToManyRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.Entity1LogicalName, "Entity1LogicalName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.Entity2LogicalName, "Entity2LogicalName"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));

        ThrowIfErrors(messages);
    }

    /// <summary>
    /// Validates a <see cref="CreateGlobalChoiceRequest"/>.
    /// </summary>
    public void ValidateCreateGlobalChoiceRequest(CreateGlobalChoiceRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.DisplayName, "DisplayName"));

        if (request.Options != null)
        {
            var duplicateValues = request.Options
                .GroupBy(o => o.Value)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateValues.Count > 0)
            {
                messages.Add(new ValidationMessage
                {
                    Field = "Options",
                    Rule = MetadataErrorCodes.DuplicateOptionValue,
                    Message = $"Duplicate option values: {string.Join(", ", duplicateValues)}."
                });
            }
        }

        ThrowIfErrors(messages);
    }

    /// <summary>
    /// Validates a <see cref="CreateKeyRequest"/>. Key attributes must be between 1 and 16.
    /// </summary>
    public void ValidateCreateKeyRequest(CreateKeyRequest request, string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ValidationMessage>();

        CollectIfInvalid(messages, () => ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName"));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName"));
        CollectIfInvalid(messages, () => ValidateSchemaName(request.SchemaName));
        CollectIfInvalid(messages, () => ValidatePrefix(request.SchemaName, publisherPrefix));
        CollectIfInvalid(messages, () => ValidateRequiredString(request.DisplayName, "DisplayName"));

        if (request.KeyAttributes == null || request.KeyAttributes.Length == 0)
        {
            messages.Add(new ValidationMessage
            {
                Field = "KeyAttributes",
                Rule = MetadataErrorCodes.InvalidKeyAttributeCount,
                Message = "At least 1 key attribute is required."
            });
        }
        else if (request.KeyAttributes.Length > 16)
        {
            messages.Add(new ValidationMessage
            {
                Field = "KeyAttributes",
                Rule = MetadataErrorCodes.InvalidKeyAttributeCount,
                Message = "A maximum of 16 key attributes is allowed."
            });
        }

        ThrowIfErrors(messages);
    }

    private static void CollectIfInvalid(List<ValidationMessage> messages, Action validate)
    {
        try
        {
            validate();
        }
        catch (MetadataValidationException ex)
        {
            messages.AddRange(ex.ValidationMessages);
        }
    }

    private static void ThrowIfErrors(List<ValidationMessage> messages)
    {
        if (messages.Count > 0)
        {
            var summary = string.Join("; ", messages.Select(m => m.Message));
            throw new MetadataValidationException(
                messages[0].Rule,
                $"Validation failed: {summary}",
                messages);
        }
    }
}
