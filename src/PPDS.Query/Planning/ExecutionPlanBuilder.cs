using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Transpilation;

namespace PPDS.Query.Planning;

/// <summary>
/// Builds execution plans from ScriptDom AST nodes.
/// Uses the TSqlFragmentVisitor pattern to walk the AST and produce
/// existing plan nodes from PPDS.Dataverse.Query.Planning.Nodes.
/// </summary>
public sealed class ExecutionPlanBuilder : TSqlFragmentVisitor
{
    private readonly FetchXmlGenerator _fetchXmlGenerator;
    private readonly QueryPlanOptions _options;

    private IQueryPlanNode? _resultNode;
    private string _resultFetchXml = "";
    private IReadOnlyDictionary<string, VirtualColumnInfo> _resultVirtualColumns =
        new Dictionary<string, VirtualColumnInfo>();
    private string _resultEntityName = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
    /// </summary>
    /// <param name="options">Planning options for the query.</param>
    public ExecutionPlanBuilder(QueryPlanOptions? options = null)
    {
        _options = options ?? new QueryPlanOptions();
        _fetchXmlGenerator = new FetchXmlGenerator();
    }

    /// <summary>
    /// Builds an execution plan from a TSqlFragment AST.
    /// </summary>
    /// <param name="fragment">The parsed SQL fragment (TSqlScript, TSqlStatement, etc.).</param>
    /// <returns>The query plan result with root node and metadata.</returns>
    public QueryPlanResult Build(TSqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        // Reset state
        _resultNode = null;
        _resultFetchXml = "";
        _resultVirtualColumns = new Dictionary<string, VirtualColumnInfo>();
        _resultEntityName = "";

        // Visit the AST
        fragment.Accept(this);

        if (_resultNode == null)
        {
            throw new InvalidOperationException("No execution plan was generated. " +
                "The SQL statement may not be supported.");
        }

        return new QueryPlanResult
        {
            RootNode = _resultNode,
            FetchXml = _resultFetchXml,
            VirtualColumns = _resultVirtualColumns,
            EntityLogicalName = _resultEntityName
        };
    }

    /// <summary>
    /// Builds an execution plan from a TSqlScript containing one or more statements.
    /// Uses the first statement in the first batch.
    /// </summary>
    public QueryPlanResult BuildFromScript(TSqlScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (script.Batches.Count == 0 || script.Batches[0].Statements.Count == 0)
        {
            throw new InvalidOperationException("SQL script contains no statements.");
        }

        var statement = script.Batches[0].Statements[0];
        return Build(statement);
    }

    #region Visitor Methods

    /// <summary>
    /// Visits a SelectStatement and builds a plan for it.
    /// </summary>
    public override void ExplicitVisit(SelectStatement node)
    {
        var planResult = PlanSelect(node);
        _resultNode = planResult.RootNode;
        _resultFetchXml = planResult.FetchXml;
        _resultVirtualColumns = planResult.VirtualColumns;
        _resultEntityName = planResult.EntityLogicalName;
    }

    /// <summary>
    /// Visits an InsertStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(InsertStatement node)
    {
        var planResult = PlanInsert(node);
        _resultNode = planResult.RootNode;
        _resultFetchXml = planResult.FetchXml;
        _resultVirtualColumns = planResult.VirtualColumns;
        _resultEntityName = planResult.EntityLogicalName;
    }

    /// <summary>
    /// Visits an UpdateStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(UpdateStatement node)
    {
        var planResult = PlanUpdate(node);
        _resultNode = planResult.RootNode;
        _resultFetchXml = planResult.FetchXml;
        _resultVirtualColumns = planResult.VirtualColumns;
        _resultEntityName = planResult.EntityLogicalName;
    }

    /// <summary>
    /// Visits a DeleteStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(DeleteStatement node)
    {
        var planResult = PlanDelete(node);
        _resultNode = planResult.RootNode;
        _resultFetchXml = planResult.FetchXml;
        _resultVirtualColumns = planResult.VirtualColumns;
        _resultEntityName = planResult.EntityLogicalName;
    }

    #endregion

    #region SELECT Planning

    private QueryPlanResult PlanSelect(SelectStatement selectStatement)
    {
        var querySpec = selectStatement.QueryExpression as QuerySpecification
            ?? throw new InvalidOperationException("Expected QuerySpecification in SelectStatement");

        // Generate FetchXML via the generator
        var transpileResult = _fetchXmlGenerator.Generate(selectStatement);

        // Get entity name from FROM clause
        var entityName = GetEntityName(querySpec);

        // Check for metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(querySpec, entityName);
        }

        // Get TOP value
        var topValue = GetTopValue(querySpec);

        // Check for caller-controlled paging
        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;

        // Create the base scan node
        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows ?? topValue,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie,
            includeCount: _options.IncludeCount);

        IQueryPlanNode rootNode = scanNode;

        // Add PrefetchScanNode if enabled and appropriate
        var hasAggregates = HasAggregates(querySpec.SelectElements);
        if (_options.EnablePrefetch && !hasAggregates && !isCallerPaged)
        {
            rootNode = new PrefetchScanNode(rootNode, _options.PrefetchBufferSize);
        }

        // Client-side filter for expressions that can't be pushed to FetchXML
        var clientFilterCondition = ExtractClientFilterCondition(querySpec.WhereClause);
        if (clientFilterCondition != null)
        {
            rootNode = new ClientFilterNode(rootNode, clientFilterCondition);
        }

        // HAVING clause - client-side filter after aggregate
        if (querySpec.HavingClause != null)
        {
            var havingCondition = ConvertToSqlCondition(querySpec.HavingClause.SearchCondition);
            if (havingCondition != null)
            {
                rootNode = new ClientFilterNode(rootNode, havingCondition);
            }
        }

        // Computed columns (CASE, IIF) - add ProjectNode
        if (HasComputedColumns(querySpec.SelectElements))
        {
            rootNode = BuildProjectNode(rootNode, querySpec.SelectElements);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = entityName
        };
    }

    private QueryPlanResult PlanMetadataQuery(QuerySpecification querySpec, string entityName)
    {
        // Extract the table name after "metadata." prefix
        var metadataTable = entityName;
        if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            metadataTable = metadataTable["metadata.".Length..];
        }

        // Extract requested column names
        List<string>? requestedColumns = null;
        if (querySpec.SelectElements.Count > 0)
        {
            var hasWildcard = querySpec.SelectElements.Any(e => e is SelectStarExpression);
            if (!hasWildcard)
            {
                requestedColumns = new List<string>();
                foreach (var element in querySpec.SelectElements)
                {
                    if (element is SelectScalarExpression scalar &&
                        scalar.Expression is ColumnReferenceExpression colRef)
                    {
                        var columnName = GetColumnName(colRef);
                        requestedColumns.Add(scalar.ColumnName?.Value ?? columnName);
                    }
                }
            }
        }

        // Convert WHERE clause to ISqlCondition
        ISqlCondition? whereCondition = null;
        if (querySpec.WhereClause != null)
        {
            whereCondition = ConvertToSqlCondition(querySpec.WhereClause.SearchCondition);
        }

        var scanNode = new MetadataScanNode(
            metadataTable,
            metadataExecutor: null, // Resolved at execution time
            requestedColumns,
            whereCondition);

        return new QueryPlanResult
        {
            RootNode = scanNode,
            FetchXml = $"-- Metadata query: {metadataTable}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = metadataTable
        };
    }

    #endregion

    #region INSERT Planning

    private QueryPlanResult PlanInsert(InsertStatement insertStatement)
    {
        // Get target entity name
        var targetSpec = insertStatement.InsertSpecification;
        var targetTable = targetSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("INSERT target must be a named table");

        var entityName = GetTableName(targetTable);

        // Get column names
        var columns = new List<string>();
        foreach (var col in targetSpec.Columns)
        {
            columns.Add(GetColumnName(col));
        }

        IQueryPlanNode rootNode;

        if (targetSpec.InsertSource is ValuesInsertSource valuesSource)
        {
            // INSERT VALUES
            var valueRows = new List<IReadOnlyList<ISqlExpression>>();
            foreach (var row in valuesSource.RowValues)
            {
                var expressions = new List<ISqlExpression>();
                foreach (var expr in row.ColumnValues)
                {
                    expressions.Add(ConvertToSqlExpression(expr));
                }
                valueRows.Add(expressions);
            }

            rootNode = DmlExecuteNode.InsertValues(
                entityName,
                columns,
                valueRows,
                rowCap: _options.DmlRowCap ?? int.MaxValue);
        }
        else if (targetSpec.InsertSource is SelectInsertSource selectSource)
        {
            // INSERT SELECT
            var sourceResult = PlanSelect(new SelectStatement { QueryExpression = selectSource.Select });
            var sourceColumns = ExtractSelectColumnNames(selectSource.Select);

            rootNode = DmlExecuteNode.InsertSelect(
                entityName,
                columns,
                sourceResult.RootNode,
                sourceColumns: sourceColumns,
                rowCap: _options.DmlRowCap ?? int.MaxValue);
        }
        else
        {
            throw new NotSupportedException("INSERT source type not supported");
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- DML: INSERT INTO {entityName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region UPDATE Planning

    private QueryPlanResult PlanUpdate(UpdateStatement updateStatement)
    {
        var updateSpec = updateStatement.UpdateSpecification;
        var targetTable = updateSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("UPDATE target must be a named table");

        var entityName = GetTableName(targetTable);
        var idColumn = entityName + "id";

        // Build SET clauses
        var setClauses = new List<SqlSetClause>();
        var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clause in updateSpec.SetClauses)
        {
            if (clause is AssignmentSetClause assignment)
            {
                var colRef = assignment.Column;
                var columnName = GetColumnName(colRef);
                var expression = ConvertToSqlExpression(assignment.NewValue);
                setClauses.Add(new SqlSetClause(columnName, expression));

                // Collect columns referenced in expressions
                CollectReferencedColumns(assignment.NewValue, referencedColumns);
            }
        }

        // Build SELECT to find matching records
        // SELECT entityid, [columns referenced in SET] FROM entity WHERE ...
        var selectColumns = new List<ISqlSelectColumn> { SqlColumnRef.Simple(idColumn) };
        foreach (var col in referencedColumns)
        {
            if (!col.Equals(idColumn, StringComparison.OrdinalIgnoreCase))
            {
                selectColumns.Add(SqlColumnRef.Simple(col));
            }
        }

        // Convert WHERE clause
        ISqlCondition? whereCondition = null;
        if (updateSpec.WhereClause != null)
        {
            whereCondition = ConvertToSqlCondition(updateSpec.WhereClause.SearchCondition);
        }

        // Create source SELECT statement AST (using old AST types for QueryPlanner compatibility)
        var sourceSelect = new SqlSelectStatement(
            selectColumns,
            new SqlTableRef(entityName),
            where: whereCondition);

        // Plan the source SELECT using old planner for compatibility
        var sourceTranspileResult = new Dataverse.Sql.Transpilation.SqlToFetchXmlTranspiler()
            .TranspileWithVirtualColumns(sourceSelect);

        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;
        var scanNode = new FetchXmlScanNode(
            sourceTranspileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie);

        var rootNode = DmlExecuteNode.Update(
            entityName,
            scanNode,
            setClauses,
            rowCap: _options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = sourceTranspileResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region DELETE Planning

    private QueryPlanResult PlanDelete(DeleteStatement deleteStatement)
    {
        var deleteSpec = deleteStatement.DeleteSpecification;
        var targetTable = deleteSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("DELETE target must be a named table");

        var entityName = GetTableName(targetTable);
        var idColumn = entityName + "id";

        // Convert WHERE clause
        ISqlCondition? whereCondition = null;
        if (deleteSpec.WhereClause != null)
        {
            whereCondition = ConvertToSqlCondition(deleteSpec.WhereClause.SearchCondition);
        }

        // Build SELECT to find record IDs
        var sourceSelect = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple(idColumn) },
            new SqlTableRef(entityName),
            where: whereCondition);

        var sourceTranspileResult = new Dataverse.Sql.Transpilation.SqlToFetchXmlTranspiler()
            .TranspileWithVirtualColumns(sourceSelect);

        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;
        var scanNode = new FetchXmlScanNode(
            sourceTranspileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie);

        var rootNode = DmlExecuteNode.Delete(
            entityName,
            scanNode,
            rowCap: _options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = sourceTranspileResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region ProjectNode Building

    private static bool HasComputedColumns(IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar)
            {
                switch (scalar.Expression)
                {
                    case SearchedCaseExpression:
                    case SimpleCaseExpression:
                    case IIfCall:
                        return true;
                    case FunctionCall func when !IsAggregateFunction(func):
                        // Non-aggregate functions are computed client-side
                        return true;
                }
            }
        }
        return false;
    }

    private static ProjectNode BuildProjectNode(IQueryPlanNode input, IList<SelectElement> selectElements)
    {
        var projections = new List<ProjectColumn>();

        foreach (var element in selectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    // Wildcard is handled at scan level
                    break;

                case SelectScalarExpression scalar:
                {
                    var outputName = scalar.ColumnName?.Value ?? GetExpressionAlias(scalar.Expression);

                    switch (scalar.Expression)
                    {
                        case ColumnReferenceExpression:
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;

                        case FunctionCall func when IsAggregateFunction(func):
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;

                        case SearchedCaseExpression:
                        case SimpleCaseExpression:
                        case IIfCall:
                        case FunctionCall:
                        case BinaryExpression:
                        {
                            var expr = ConvertToSqlExpression(scalar.Expression);
                            projections.Add(ProjectColumn.Computed(outputName, expr));
                            break;
                        }

                        default:
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;
                    }
                    break;
                }
            }
        }

        return new ProjectNode(input, projections);
    }

    private static string GetExpressionAlias(ScalarExpression expression)
    {
        return expression switch
        {
            ColumnReferenceExpression colRef => GetColumnName(colRef),
            FunctionCall func => func.FunctionName.Value.ToLowerInvariant(),
            _ => "computed"
        };
    }

    #endregion

    #region Client Filter Extraction

    /// <summary>
    /// Extracts conditions from WHERE clause that must be evaluated client-side
    /// (column-to-column comparisons, computed expressions).
    /// </summary>
    private static ISqlCondition? ExtractClientFilterCondition(WhereClause? whereClause)
    {
        if (whereClause == null) return null;

        var clientConditions = new List<ISqlCondition>();
        ExtractClientConditionsRecursive(whereClause.SearchCondition, clientConditions);

        return clientConditions.Count switch
        {
            0 => null,
            1 => clientConditions[0],
            _ => new SqlLogicalCondition(SqlLogicalOperator.And, clientConditions)
        };
    }

    private static void ExtractClientConditionsRecursive(
        BooleanExpression condition,
        List<ISqlCondition> clientConditions)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
                // Check if this is a column-to-column comparison
                if (comparison.FirstExpression is ColumnReferenceExpression &&
                    comparison.SecondExpression is ColumnReferenceExpression)
                {
                    var sqlCondition = ConvertToSqlCondition(comparison);
                    if (sqlCondition != null)
                    {
                        clientConditions.Add(sqlCondition);
                    }
                }
                // Check if this uses expressions on either side
                else if (!IsSimpleLiteralComparison(comparison))
                {
                    var sqlCondition = ConvertToSqlCondition(comparison);
                    if (sqlCondition != null)
                    {
                        clientConditions.Add(sqlCondition);
                    }
                }
                break;

            case BooleanBinaryExpression binary:
                ExtractClientConditionsRecursive(binary.FirstExpression, clientConditions);
                ExtractClientConditionsRecursive(binary.SecondExpression, clientConditions);
                break;

            case BooleanParenthesisExpression paren:
                ExtractClientConditionsRecursive(paren.Expression, clientConditions);
                break;
        }
    }

    private static bool IsSimpleLiteralComparison(BooleanComparisonExpression comparison)
    {
        return comparison.FirstExpression is ColumnReferenceExpression &&
               comparison.SecondExpression is Literal;
    }

    #endregion

    #region AST Conversion Helpers

    private static ISqlCondition? ConvertToSqlCondition(BooleanExpression condition)
    {
        return condition switch
        {
            BooleanComparisonExpression comparison => ConvertComparison(comparison),
            BooleanBinaryExpression binary => ConvertLogical(binary),
            BooleanIsNullExpression isNull => ConvertIsNull(isNull),
            LikePredicate like => ConvertLike(like),
            InPredicate inPred => ConvertIn(inPred),
            BooleanParenthesisExpression paren => ConvertToSqlCondition(paren.Expression),
            BooleanNotExpression not => ConvertNot(not),
            _ => null
        };
    }

    private static ISqlCondition? ConvertComparison(BooleanComparisonExpression comparison)
    {
        var op = comparison.ComparisonType switch
        {
            BooleanComparisonType.Equals => SqlComparisonOperator.Equal,
            BooleanComparisonType.NotEqualToBrackets => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.NotEqualToExclamation => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.LessThan => SqlComparisonOperator.LessThan,
            BooleanComparisonType.GreaterThan => SqlComparisonOperator.GreaterThan,
            BooleanComparisonType.LessThanOrEqualTo => SqlComparisonOperator.LessThanOrEqual,
            BooleanComparisonType.GreaterThanOrEqualTo => SqlComparisonOperator.GreaterThanOrEqual,
            _ => SqlComparisonOperator.Equal
        };

        if (comparison.FirstExpression is ColumnReferenceExpression colRef &&
            comparison.SecondExpression is Literal literal)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            var value = ConvertLiteral(literal);

            return new SqlComparisonCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                op,
                value);
        }

        // Column-to-column or expression comparison
        if (comparison.FirstExpression is ScalarExpression leftExpr &&
            comparison.SecondExpression is ScalarExpression rightExpr)
        {
            var left = ConvertToSqlExpression(leftExpr);
            var right = ConvertToSqlExpression(rightExpr);
            return new SqlExpressionCondition(left, op, right);
        }

        return null;
    }

    private static ISqlCondition ConvertLogical(BooleanBinaryExpression binary)
    {
        var op = binary.BinaryExpressionType == BooleanBinaryExpressionType.Or
            ? SqlLogicalOperator.Or
            : SqlLogicalOperator.And;

        var conditions = new List<ISqlCondition>();

        var left = ConvertToSqlCondition(binary.FirstExpression);
        if (left != null) conditions.Add(left);

        var right = ConvertToSqlCondition(binary.SecondExpression);
        if (right != null) conditions.Add(right);

        return new SqlLogicalCondition(op, conditions);
    }

    private static ISqlCondition? ConvertIsNull(BooleanIsNullExpression isNull)
    {
        if (isNull.Expression is ColumnReferenceExpression colRef)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            return new SqlNullCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                isNull.IsNot);
        }
        return null;
    }

    private static ISqlCondition? ConvertLike(LikePredicate like)
    {
        if (like.FirstExpression is ColumnReferenceExpression colRef &&
            like.SecondExpression is StringLiteral pattern)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            return new SqlLikeCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                pattern.Value,
                like.NotDefined);
        }
        return null;
    }

    private static ISqlCondition? ConvertIn(InPredicate inPred)
    {
        if (inPred.Expression is ColumnReferenceExpression colRef)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            var values = new List<SqlLiteral>();

            foreach (var value in inPred.Values)
            {
                if (value is Literal literal)
                {
                    values.Add(ConvertLiteral(literal));
                }
            }

            return new SqlInCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                values,
                inPred.NotDefined);
        }
        return null;
    }

    private static ISqlCondition? ConvertNot(BooleanNotExpression not)
    {
        // For NOT, we invert the inner condition
        var inner = ConvertToSqlCondition(not.Expression);
        if (inner is SqlNullCondition nullCond)
        {
            return new SqlNullCondition(nullCond.Column, !nullCond.IsNegated);
        }
        if (inner is SqlLikeCondition likeCond)
        {
            return new SqlLikeCondition(likeCond.Column, likeCond.Pattern, !likeCond.IsNegated);
        }
        if (inner is SqlInCondition inCond)
        {
            return new SqlInCondition(inCond.Column, inCond.Values, !inCond.IsNegated);
        }
        return inner;
    }

    private static ISqlExpression ConvertToSqlExpression(ScalarExpression expression)
    {
        return expression switch
        {
            ColumnReferenceExpression colRef => ConvertColumnRef(colRef),
            IntegerLiteral intLit => new SqlLiteralExpression(SqlLiteral.Number(intLit.Value)),
            NumericLiteral numLit => new SqlLiteralExpression(SqlLiteral.Number(numLit.Value)),
            RealLiteral realLit => new SqlLiteralExpression(SqlLiteral.Number(realLit.Value)),
            StringLiteral strLit => new SqlLiteralExpression(SqlLiteral.String(strLit.Value)),
            NullLiteral => new SqlLiteralExpression(SqlLiteral.Null()),
            BinaryExpression binary => ConvertBinaryExpression(binary),
            UnaryExpression unary => ConvertUnaryExpression(unary),
            FunctionCall func => ConvertFunctionCall(func),
            SearchedCaseExpression caseExpr => ConvertSearchedCase(caseExpr),
            SimpleCaseExpression simpleCase => ConvertSimpleCase(simpleCase),
            IIfCall iif => ConvertIif(iif),
            CastCall cast => ConvertCast(cast),
            ConvertCall convert => ConvertConvert(convert),
            ParenthesisExpression paren => ConvertToSqlExpression(paren.Expression),
            _ => new SqlLiteralExpression(SqlLiteral.Null()) // Fallback
        };
    }

    private static SqlColumnExpression ConvertColumnRef(ColumnReferenceExpression colRef)
    {
        var columnName = GetColumnName(colRef);
        var tableName = GetTableQualifier(colRef);
        return new SqlColumnExpression(new SqlColumnRef(tableName, columnName, null, false));
    }

    private static SqlBinaryExpression ConvertBinaryExpression(BinaryExpression binary)
    {
        var op = binary.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => SqlBinaryOperator.Add,
            BinaryExpressionType.Subtract => SqlBinaryOperator.Subtract,
            BinaryExpressionType.Multiply => SqlBinaryOperator.Multiply,
            BinaryExpressionType.Divide => SqlBinaryOperator.Divide,
            BinaryExpressionType.Modulo => SqlBinaryOperator.Modulo,
            _ => SqlBinaryOperator.Add
        };

        var left = ConvertToSqlExpression(binary.FirstExpression);
        var right = ConvertToSqlExpression(binary.SecondExpression);
        return new SqlBinaryExpression(left, op, right);
    }

    private static SqlUnaryExpression ConvertUnaryExpression(UnaryExpression unary)
    {
        var op = unary.UnaryExpressionType switch
        {
            UnaryExpressionType.Negative => SqlUnaryOperator.Negate,
            UnaryExpressionType.Positive => SqlUnaryOperator.Negate, // Positive is a no-op
            _ => SqlUnaryOperator.Negate
        };

        var operand = ConvertToSqlExpression(unary.Expression);
        return new SqlUnaryExpression(op, operand);
    }

    private static ISqlExpression ConvertFunctionCall(FunctionCall func)
    {
        var funcName = func.FunctionName.Value;
        var args = new List<ISqlExpression>();

        foreach (var param in func.Parameters)
        {
            if (param is ScalarExpression scalar)
            {
                args.Add(ConvertToSqlExpression(scalar));
            }
        }

        return new SqlFunctionExpression(funcName, args);
    }

    private static SqlCaseExpression ConvertSearchedCase(SearchedCaseExpression caseExpr)
    {
        var whenClauses = new List<SqlWhenClause>();

        foreach (var when in caseExpr.WhenClauses)
        {
            var condition = ConvertToSqlCondition(when.WhenExpression);
            var result = ConvertToSqlExpression(when.ThenExpression);

            if (condition != null)
            {
                whenClauses.Add(new SqlWhenClause(condition, result));
            }
        }

        var elseExpr = caseExpr.ElseExpression != null
            ? ConvertToSqlExpression(caseExpr.ElseExpression)
            : null;

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    private static SqlCaseExpression ConvertSimpleCase(SimpleCaseExpression simpleCase)
    {
        var inputExpr = ConvertToSqlExpression(simpleCase.InputExpression);
        var whenClauses = new List<SqlWhenClause>();

        foreach (var when in simpleCase.WhenClauses)
        {
            // Simple CASE: WHEN value THEN result
            // Convert to: WHEN input = value THEN result
            var whenValue = ConvertToSqlExpression(when.WhenExpression);
            var condition = new SqlExpressionCondition(inputExpr, SqlComparisonOperator.Equal, whenValue);
            var result = ConvertToSqlExpression(when.ThenExpression);
            whenClauses.Add(new SqlWhenClause(condition, result));
        }

        var elseExpr = simpleCase.ElseExpression != null
            ? ConvertToSqlExpression(simpleCase.ElseExpression)
            : null;

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    private static SqlIifExpression ConvertIif(IIfCall iif)
    {
        var condition = ConvertToSqlCondition(iif.Predicate);
        var trueValue = ConvertToSqlExpression(iif.ThenExpression);
        var falseValue = ConvertToSqlExpression(iif.ElseExpression);

        // IIF needs a valid condition - fallback if conversion failed
        condition ??= new SqlComparisonCondition(
            SqlColumnRef.Simple("_dummy"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));

        return new SqlIifExpression(condition, trueValue, falseValue);
    }

    private static SqlCastExpression ConvertCast(CastCall cast)
    {
        var expr = ConvertToSqlExpression(cast.Parameter);
        var targetType = GetDataTypeName(cast.DataType);
        return new SqlCastExpression(expr, targetType);
    }

    private static SqlCastExpression ConvertConvert(ConvertCall convert)
    {
        var expr = ConvertToSqlExpression(convert.Parameter);
        var targetType = GetDataTypeName(convert.DataType);
        int? style = null;

        if (convert.Style is IntegerLiteral styleLit)
        {
            style = int.Parse(styleLit.Value);
        }

        return new SqlCastExpression(expr, targetType, style);
    }

    private static string GetDataTypeName(DataTypeReference dataType)
    {
        return dataType switch
        {
            SqlDataTypeReference sqlType => sqlType.SqlDataTypeOption.ToString().ToLowerInvariant(),
            _ => "varchar"
        };
    }

    private static SqlLiteral ConvertLiteral(Literal literal)
    {
        return literal switch
        {
            NullLiteral => SqlLiteral.Null(),
            StringLiteral str => SqlLiteral.String(str.Value),
            IntegerLiteral intLit => SqlLiteral.Number(intLit.Value),
            NumericLiteral numLit => SqlLiteral.Number(numLit.Value),
            RealLiteral realLit => SqlLiteral.Number(realLit.Value),
            _ => SqlLiteral.String(literal.Value ?? "")
        };
    }

    #endregion

    #region Helper Methods

    private static string GetEntityName(QuerySpecification querySpec)
    {
        if (querySpec.FromClause?.TableReferences.Count > 0)
        {
            var tableRef = querySpec.FromClause.TableReferences[0];
            return GetEntityNameFromTableRef(tableRef);
        }
        throw new InvalidOperationException("SELECT statement must have a FROM clause");
    }

    private static string GetEntityNameFromTableRef(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference namedTable => GetTableName(namedTable),
            QualifiedJoin qualifiedJoin => GetEntityNameFromTableRef(qualifiedJoin.FirstTableReference),
            _ => throw new NotSupportedException($"Table reference type {tableRef.GetType().Name} not supported")
        };
    }

    private static string GetTableName(NamedTableReference namedTable)
    {
        return namedTable.SchemaObject.BaseIdentifier?.Value
            ?? namedTable.SchemaObject.Identifiers.Last().Value;
    }

    private static string GetColumnName(ColumnReferenceExpression colRef)
    {
        if (colRef.ColumnType == ColumnType.Wildcard)
        {
            return "*";
        }
        return colRef.MultiPartIdentifier.Identifiers.Last().Value;
    }

    private static string? GetTableQualifier(ColumnReferenceExpression colRef)
    {
        var identifiers = colRef.MultiPartIdentifier.Identifiers;
        if (identifiers.Count >= 2)
        {
            return identifiers[^2].Value;
        }
        return null;
    }

    private static int? GetTopValue(QuerySpecification querySpec)
    {
        if (querySpec.TopRowFilter?.Expression is IntegerLiteral intLiteral)
        {
            return int.Parse(intLiteral.Value);
        }
        return null;
    }

    private static bool HasAggregates(IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.Expression is FunctionCall func &&
                IsAggregateFunction(func))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAggregateFunction(FunctionCall func)
    {
        var name = func.FunctionName.Value.ToUpperInvariant();
        return name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
    }

    private static List<string> ExtractSelectColumnNames(QueryExpression queryExpr)
    {
        var names = new List<string>();

        if (queryExpr is QuerySpecification querySpec)
        {
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectScalarExpression scalar)
                {
                    if (scalar.ColumnName != null)
                    {
                        names.Add(scalar.ColumnName.Value);
                    }
                    else if (scalar.Expression is ColumnReferenceExpression colRef)
                    {
                        names.Add(GetColumnName(colRef));
                    }
                    else if (scalar.Expression is FunctionCall func)
                    {
                        names.Add(func.FunctionName.Value.ToLowerInvariant());
                    }
                    else
                    {
                        names.Add("computed");
                    }
                }
            }
        }

        return names;
    }

    private static void CollectReferencedColumns(ScalarExpression expression, HashSet<string> columns)
    {
        switch (expression)
        {
            case ColumnReferenceExpression colRef:
                columns.Add(GetColumnName(colRef));
                break;

            case BinaryExpression binary:
                CollectReferencedColumns(binary.FirstExpression, columns);
                CollectReferencedColumns(binary.SecondExpression, columns);
                break;

            case UnaryExpression unary:
                CollectReferencedColumns(unary.Expression, columns);
                break;

            case FunctionCall func:
                foreach (var param in func.Parameters)
                {
                    if (param is ScalarExpression scalar)
                    {
                        CollectReferencedColumns(scalar, columns);
                    }
                }
                break;

            case ParenthesisExpression paren:
                CollectReferencedColumns(paren.Expression, columns);
                break;
        }
    }

    #endregion
}
