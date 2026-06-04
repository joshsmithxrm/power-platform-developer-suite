using System.Collections.Generic;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Maps Dataverse AttributeType strings to formxml control classid GUIDs.
/// </summary>
internal static class ClassIdResolver
{
    /// <summary>The classid for sub-grid controls.</summary>
    public const string SubgridClassId = "{E7A81278-8635-4d9e-8D4D-59480B391C5B}";

    private static readonly Dictionary<string, string> ClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["String"]   = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}",
        ["Money"]    = "{533B9108-5A8B-42cb-BD37-52D1B8E7C741}",
        ["Picklist"] = "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}",
        ["Lookup"]   = "{270BD3DB-D9AF-4782-9025-509E298DEC0A}",
        ["DateTime"] = "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}",
        ["Integer"]  = "{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}",
        ["Decimal"]  = "{C3EFE0C3-0EC6-42be-8349-CBD9079C5A6F}",
        ["Boolean"]  = "{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}",
        ["Memo"]     = "{E0DECE4B-6FC8-4a8f-A065-082708572369}",
    };

    private static readonly string SupportedTypesList =
        string.Join(", ", ClassIds.Keys);

    /// <summary>
    /// Resolves the formxml classid for a given <see cref="AttributeMetadataDto.AttributeType"/> string.
    /// </summary>
    /// <param name="attributeType">The AttributeType string from <see cref="PPDS.Dataverse.Metadata.Models.AttributeMetadataDto"/>.</param>
    /// <returns>The classid GUID string in brace format.</returns>
    /// <exception cref="PpdsException">Thrown with <see cref="FormErrorCodes.UnsupportedColumnType"/> when the type has no mapping.</exception>
    internal static string ResolveForField(string attributeType)
    {
        if (ClassIds.TryGetValue(attributeType, out var classId))
            return classId;

        throw new PpdsException(
            FormErrorCodes.UnsupportedColumnType,
            $"Column type '{attributeType}' is not supported for form controls. Supported types: {SupportedTypesList}.");
    }
}
