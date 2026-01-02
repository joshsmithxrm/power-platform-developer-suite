namespace PPDS.Migration.Import
{
    /// <summary>
    /// Represents the create/update validity of a field in the target environment.
    /// </summary>
    /// <param name="IsValidForCreate">Whether the field can be set during create operations.</param>
    /// <param name="IsValidForUpdate">Whether the field can be set during update operations.</param>
    public readonly record struct FieldValidity(bool IsValidForCreate, bool IsValidForUpdate);
}
