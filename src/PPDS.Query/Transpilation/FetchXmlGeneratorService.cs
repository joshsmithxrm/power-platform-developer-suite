using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Planning;
using DataverseTranspileResult = PPDS.Dataverse.Sql.Transpilation.TranspileResult;
using DataverseVirtualColumnInfo = PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo;

namespace PPDS.Query.Transpilation;

/// <summary>
/// Adapts <see cref="FetchXmlGenerator"/> to the <see cref="IFetchXmlGeneratorService"/> contract
/// expected by <see cref="ExecutionPlanBuilder"/>.
/// </summary>
/// <remarks>
/// The generator produces <see cref="TranspileResult"/> (PPDS.Query.Transpilation namespace),
/// but the service interface returns <see cref="DataverseTranspileResult"/> (PPDS.Dataverse.Sql.Transpilation).
/// This class maps between the two, converting virtual column metadata along the way.
/// </remarks>
public sealed class FetchXmlGeneratorService : IFetchXmlGeneratorService
{
    /// <inheritdoc />
    public DataverseTranspileResult Generate(TSqlFragment statement)
    {
        var generator = new FetchXmlGenerator();
        var queryResult = generator.GenerateWithVirtualColumns(statement);

        // Map PPDS.Query VirtualColumnInfo → PPDS.Dataverse VirtualColumnInfo
        var mappedVirtualColumns = new Dictionary<string, DataverseVirtualColumnInfo>(
            queryResult.VirtualColumns.Count);

        foreach (var kvp in queryResult.VirtualColumns)
        {
            mappedVirtualColumns[kvp.Key] = new DataverseVirtualColumnInfo
            {
                BaseColumnName = kvp.Value.BaseColumnName,
                BaseColumnExplicitlyQueried = kvp.Value.BaseColumnExplicitlyQueried,
                Alias = kvp.Value.Alias
            };
        }

        return new DataverseTranspileResult
        {
            FetchXml = queryResult.FetchXml,
            VirtualColumns = mappedVirtualColumns
        };
    }
}
