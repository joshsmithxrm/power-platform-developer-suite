using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Query.Planning;

/// <summary>
/// Service interface for generating FetchXML from a ScriptDom AST fragment.
/// Injected into <see cref="ExecutionPlanBuilder"/> to decouple plan construction
/// from the FetchXML transpilation implementation (wired in a later phase).
/// </summary>
/// <remarks>
/// Returns <see cref="TranspileResult"/> from <c>PPDS.Dataverse.Sql.Transpilation</c>
/// because the plan nodes and <see cref="PPDS.Dataverse.Query.Planning.QueryPlanResult"/>
/// use that type. Implementors should produce a Dataverse-namespace TranspileResult.
/// </remarks>
public interface IFetchXmlGeneratorService
{
    /// <summary>
    /// Generates FetchXML and virtual column metadata from a ScriptDom statement fragment.
    /// </summary>
    /// <param name="statement">The ScriptDom AST fragment (typically a <see cref="SelectStatement"/>).</param>
    /// <returns>The transpilation result containing FetchXML and virtual column info.</returns>
    TranspileResult Generate(TSqlFragment statement);
}
