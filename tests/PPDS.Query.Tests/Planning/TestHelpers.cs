using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Planning;
using ExpressionCompiler = PPDS.Query.Execution.ExpressionCompiler;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// Helpers for creating test contexts and mock dependencies for plan node tests.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a minimal QueryPlanContext with mocked dependencies for testing.
    /// </summary>
    public static QueryPlanContext CreateTestContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        return new QueryPlanContext(mockExecutor.Object);
    }

    /// <summary>
    /// Collects all rows from an IQueryPlanNode into a list.
    /// </summary>
    public static async System.Threading.Tasks.Task<List<QueryRow>> CollectRowsAsync(
        IQueryPlanNode node,
        QueryPlanContext? context = null)
    {
        context ??= CreateTestContext();
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context, CancellationToken.None))
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Creates an ExecutionPlanBuilder and ExpressionCompiler with a variable scope accessor.
    /// </summary>
    public static (ExecutionPlanBuilder builder, ExpressionCompiler compiler) CreatePlanBuilderAndCompiler(
        VariableScope scope)
    {
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

        var compiler = new ExpressionCompiler(
            variableScopeAccessor: () => scope);

        return (builder, compiler);
    }

    /// <summary>
    /// Creates a ScriptDom DeclareVariableStatement.
    /// </summary>
    public static DeclareVariableStatement MakeDeclare(string varName, string typeName, int? initialValue = null)
    {
        var decl = new DeclareVariableElement();
        decl.VariableName = new Identifier { Value = varName.TrimStart('@') };
        decl.DataType = new SqlDataTypeReference
        {
            SqlDataTypeOption = typeName.ToUpperInvariant() switch
            {
                "INT" => SqlDataTypeOption.Int,
                "NVARCHAR" => SqlDataTypeOption.NVarChar,
                _ => SqlDataTypeOption.VarChar
            }
        };
        if (initialValue.HasValue)
        {
            decl.Value = new IntegerLiteral { Value = initialValue.Value.ToString() };
        }
        var stmt = new DeclareVariableStatement();
        stmt.Declarations.Add(decl);
        return stmt;
    }

    /// <summary>
    /// Creates a ScriptDom SetVariableStatement.
    /// </summary>
    public static SetVariableStatement MakeSetVariable(string varName, ScalarExpression expression)
    {
        var stmt = new SetVariableStatement();
        stmt.Variable = new VariableReference { Name = varName };
        stmt.Expression = expression;
        return stmt;
    }

    /// <summary>
    /// Depth-first search for a node of type T in a plan tree.
    /// </summary>
    public static T? FindNode<T>(IQueryPlanNode node) where T : class, IQueryPlanNode
    {
        if (node is T match) return match;
        foreach (var child in node.Children)
        {
            var found = FindNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Checks whether a plan tree contains a node of type T.
    /// </summary>
    public static bool ContainsNodeOfType<T>(IQueryPlanNode node) where T : IQueryPlanNode
    {
        if (node is T) return true;
        return node.Children.Any(ContainsNodeOfType<T>);
    }
}
