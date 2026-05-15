namespace PPDS.Cli.Services.Schema.Models;

/// <summary>
/// Categorical kind of schema difference detected by <see cref="SchemaComparisonService"/>.
/// </summary>
public enum DiffKind
{
    /// <summary>Entity exists in source but not in target.</summary>
    MissingEntity,

    /// <summary>Entity exists in target but not in source (non-breaking).</summary>
    ExtraEntity,

    /// <summary>Attribute exists in source but not in target.</summary>
    MissingAttribute,

    /// <summary>Attribute exists in target but not in source (non-breaking).</summary>
    ExtraAttribute,

    /// <summary>Attribute type changed between source and target.</summary>
    TypeMismatch,

    /// <summary>Required level became stricter in target (e.g. Optional → SystemRequired).</summary>
    RequiredLevelStricter,

    /// <summary>String length in target is smaller than source — truncation risk.</summary>
    LengthShrunk,

    /// <summary>Decimal precision in target is smaller than source — precision loss.</summary>
    PrecisionLoss,

    /// <summary>Source has option-set values not present in target.</summary>
    MissingOptionValue,

    /// <summary>Source lookup targets an entity that the target lookup does not allow.</summary>
    LookupTargetMissing,

    /// <summary>Relationship exists in source but not in target.</summary>
    MissingRelationship
}
