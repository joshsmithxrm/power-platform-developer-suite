using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Thrown when metadata authoring validation fails.
/// </summary>
public class MetadataValidationException : Exception
{
    /// <summary>Gets the machine-readable error code.</summary>
    public string ErrorCode { get; }

    /// <summary>Gets the field that failed validation, if a single field caused the failure.</summary>
    public string? Field { get; }

    /// <summary>Gets the validation messages describing all failures.</summary>
    public IReadOnlyList<ValidationMessage> ValidationMessages { get; }

    /// <summary>
    /// Initializes a new instance for a single-field validation failure.
    /// </summary>
    public MetadataValidationException(string errorCode, string message, string? field = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Field = field;
        ValidationMessages = field != null
            ? [new ValidationMessage { Field = field, Rule = errorCode, Message = message }]
            : [];
    }

    /// <summary>
    /// Initializes a new instance for multiple validation failures.
    /// </summary>
    public MetadataValidationException(string errorCode, string message, IReadOnlyList<ValidationMessage> messages)
        : base(message)
    {
        ErrorCode = errorCode;
        ValidationMessages = messages;
    }
}
