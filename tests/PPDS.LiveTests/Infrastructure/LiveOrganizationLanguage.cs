using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Reads the connected organisation's base language (<c>organization.languagecode</c>), so live tests never assume
/// 1033: fixtures create their labels in it and assertions read it, as the authoring service writes it.
/// </summary>
public static class LiveOrganizationLanguage
{
    /// <summary>Returns <c>organization.languagecode</c>; fails when the organisation returns none.</summary>
    public static async Task<int> ReadBaseLanguageAsync(IDataverseConnectionPool pool)
    {
        await using var client = await pool.GetClientAsync();
        var org = (await client.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode"), TopCount = 1 })).Entities.FirstOrDefault();
        var lcid = org?.GetAttributeValue<int?>("languagecode");
        return lcid is > 0 ? lcid.Value : throw new InvalidOperationException("The organisation returned no base language (organization.languagecode).");
    }
}
