using System;
using System.Collections.Generic;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Recursive descent parser for SQL statements.
/// Produces AST nodes from SQL text (SELECT, INSERT, UPDATE, DELETE).
/// </summary>
/// <remarks>
/// Supported SQL:
/// - SELECT columns FROM table
/// - SELECT DISTINCT columns FROM table
/// - SELECT TOP n columns FROM table
/// - Aggregate functions: COUNT(*), COUNT(column), SUM, AVG, MIN, MAX
/// - COUNT(DISTINCT column)
/// - GROUP BY column1, column2
/// - HAVING clause (post-aggregation filter)
/// - WHERE with comparison, LIKE, IS NULL, IN, BETWEEN operators
/// - AND/OR logical operators with parentheses
/// - ORDER BY column ASC/DESC
/// - JOIN (INNER, LEFT, RIGHT)
/// - EXISTS / NOT EXISTS (SELECT ...)
/// - UNION / UNION ALL
/// - INSERT INTO entity (cols) VALUES (...) / INSERT INTO entity (cols) SELECT ...
/// - UPDATE entity SET col = expr WHERE ...
/// - DELETE FROM entity WHERE ...
/// - IF condition BEGIN...END [ELSE BEGIN...END]
/// - Multi-statement scripts with semicolons
///
/// Not Supported (for now):
/// - INTERSECT/EXCEPT
/// </remarks>
public sealed class SqlParser
{
    private readonly string _sql;
    private IReadOnlyList<SqlToken> _tokens = Array.Empty<SqlToken>();
    private IReadOnlyList<SqlComment> _comments = Array.Empty<SqlComment>();
    private int _position;
    private int _commentIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlParser"/> class.
    /// </summary>
    public SqlParser(string sql)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>
    /// Parses SQL text into a SELECT statement AST.
    /// </summary>
    /// <exception cref="SqlParseException">If parsing fails.</exception>
    public SqlSelectStatement Parse()
    {
        var statement = ParseStatement();
        if (statement is SqlSelectStatement select)
        {
            return select;
        }
        throw Error("Expected SELECT statement");
    }

    /// <summary>
    /// Parses SQL text into an AST statement (supports all statement types).
    /// For multi-statement scripts (containing DECLARE/SET/IF/ELSE or multiple
    /// statements separated by semicolons), returns a <see cref="SqlBlockStatement"/>.
    /// </summary>
    /// <exception cref="SqlParseException">If parsing fails.</exception>
    public ISqlStatement ParseStatement()
    {
        _position = 0;
        _commentIndex = 0;

        var lexer = new SqlLexer(_sql);
        var result = lexer.Tokenize();
        _tokens = result.Tokens;
        _comments = result.Comments;

        // Parse the first statement
        var firstStmt = ParseSingleStatementBody();
        SkipSemicolons();

        // If there are more statements, this is a multi-statement script
        if (!IsAtEnd())
        {
            var statements = new List<ISqlStatement> { firstStmt };
            while (!IsAtEnd())
            {
                statements.Add(ParseSingleStatementBody());
                SkipSemicolons();
            }
            return new SqlBlockStatement(statements, statements[0].SourcePosition);
        }

        return firstStmt;
    }

    /// <summary>
    /// Dispatches and parses a single statement based on the current token.
    /// Does NOT check for EOF — the caller is responsible for that.
    /// </summary>
    private ISqlStatement ParseSingleStatementBody()
    {
        if (Check(SqlTokenType.Declare))
        {
            return ParseDeclareStatement();
        }

        // SET @variable = expression (variable assignment)
        // Note: SET without @ is handled by UPDATE parsing (UPDATE ... SET col = expr)
        if (Check(SqlTokenType.Set) && PeekAt(_position + 1).Type == SqlTokenType.Variable)
        {
            return ParseSetVariableStatement();
        }

        if (Check(SqlTokenType.If))
        {
            return ParseIfStatement();
        }

        if (Check(SqlTokenType.Insert))
        {
            return ParseInsertStatement();
        }

        if (Check(SqlTokenType.Update))
        {
            return ParseUpdateStatement();
        }

        if (Check(SqlTokenType.Delete))
        {
            return ParseDeleteStatement();
        }

        var firstSelect = ParseSelectStatementWithoutEndCheck();

        // Check for UNION / UNION ALL following the first SELECT
        if (Check(SqlTokenType.Union))
        {
            return ParseUnionStatement(firstSelect);
        }

        return firstSelect;
    }

    /// <summary>
    /// Skips optional semicolons between statements.
    /// </summary>
    private void SkipSemicolons()
    {
        while (Match(SqlTokenType.Semicolon))
        {
            // consume
        }
    }

    /// <summary>
    /// Static convenience method to parse SQL into a SELECT statement.
    /// </summary>
    public static SqlSelectStatement Parse(string sql)
    {
        var parser = new SqlParser(sql);
        return parser.Parse();
    }

    /// <summary>
    /// Static convenience method to parse SQL into a statement.
    /// </summary>
    public static ISqlStatement ParseSql(string sql)
    {
        var parser = new SqlParser(sql);
        return parser.ParseStatement();
    }

    #region Leading/Trailing Comments

    /// <summary>
    /// Gets all leading comments (comments before the first token).
    /// </summary>
    private List<string> GetLeadingComments()
    {
        var leading = new List<string>();
        var firstTokenPos = _tokens.Count > 0 ? _tokens[0].Position : 0;

        while (_commentIndex < _comments.Count)
        {
            var comment = _comments[_commentIndex];
            if (comment.Position < firstTokenPos)
            {
                leading.Add(comment.Text);
                _commentIndex++;
            }
            else
            {
                break;
            }
        }

        return leading;
    }

    /// <summary>
    /// Gets the trailing comment for the element that just finished parsing.
    /// </summary>
    private string? GetTrailingComment()
    {
        if (_commentIndex >= _comments.Count)
        {
            return null;
        }

        var lastTokenEnd = Previous().Position + Previous().Value.Length;
        var nextTokenStart = Peek().Position;

        var trailingComments = new List<string>();

        while (_commentIndex < _comments.Count)
        {
            var comment = _comments[_commentIndex];
            if (comment.Position > lastTokenEnd && comment.Position < nextTokenStart)
            {
                trailingComments.Add(comment.Text);
                _commentIndex++;
            }
            else if (comment.Position >= nextTokenStart)
            {
                break;
            }
            else
            {
                _commentIndex++;
            }
        }

        return trailingComments.Count > 0 ? string.Join(" | ", trailingComments) : null;
    }

    /// <summary>
    /// Attaches a trailing comment to an AST node if one exists.
    /// </summary>
    private void AttachTrailingComment(ISqlSelectColumn node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    private void AttachTrailingComment(SqlColumnRef node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    private void AttachTrailingComment(SqlTableRef node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    private void AttachTrailingComment(SqlJoin node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    private void AttachTrailingComment(SqlOrderByItem node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    private void AttachTrailingComment(ISqlCondition node)
    {
        var comment = GetTrailingComment();
        if (comment != null)
        {
            node.TrailingComment = comment;
        }
    }

    #endregion

    #region Statement Parsing

    /// <summary>
    /// Parses a complete SELECT statement and enforces end-of-input.
    /// </summary>
    private SqlSelectStatement ParseSelectStatement()
    {
        var statement = ParseSelectStatementWithoutEndCheck();

        // Ensure we've consumed all tokens
        if (!IsAtEnd())
        {
            throw Error($"Unexpected token: {Peek().Value}");
        }

        return statement;
    }

    /// <summary>
    /// Parses a SELECT statement body without checking for end-of-input.
    /// Used by both standalone SELECT and UNION parsing.
    /// Does not consume ORDER BY or LIMIT — for UNION branches those belong
    /// to the outer statement. Individual SELECTs inside a UNION should not
    /// have their own ORDER BY.
    /// </summary>
    private SqlSelectStatement ParseSelectStatementWithoutEndCheck()
    {
        var leadingComments = GetLeadingComments();

        Expect(SqlTokenType.Select);

        // Optional DISTINCT keyword
        var distinct = Match(SqlTokenType.Distinct);

        // Optional TOP clause
        int? top = null;
        if (Match(SqlTokenType.Top))
        {
            var topToken = Expect(SqlTokenType.Number);
            top = int.Parse(topToken.Value);
        }

        // SELECT columns (may include aggregates)
        var columns = ParseSelectColumnList();

        // FROM clause
        Expect(SqlTokenType.From);
        var from = ParseTableRef();
        AttachTrailingComment(from);

        // Optional JOIN clauses
        var joins = new List<SqlJoin>();
        while (MatchJoinKeyword())
        {
            var join = ParseJoin();
            AttachTrailingComment(join);
            joins.Add(join);
        }

        // Optional WHERE clause
        ISqlCondition? where = null;
        if (Match(SqlTokenType.Where))
        {
            where = ParseCondition();
        }

        // Optional GROUP BY clause — supports both plain column refs and
        // function expressions like YEAR(createdon) for date grouping pushdown.
        var groupBy = new List<SqlColumnRef>();
        var groupByExpressions = new List<ISqlExpression>();
        if (Match(SqlTokenType.Group))
        {
            Expect(SqlTokenType.By);
            ParseGroupByItem(groupBy, groupByExpressions);

            while (Match(SqlTokenType.Comma))
            {
                if (groupBy.Count > 0)
                {
                    var prevGroupBy = groupBy[^1];
                    AttachTrailingComment(prevGroupBy);
                }
                ParseGroupByItem(groupBy, groupByExpressions);
            }

            if (groupBy.Count > 0)
            {
                var lastGroupBy = groupBy[^1];
                if (lastGroupBy.TrailingComment == null)
                {
                    AttachTrailingComment(lastGroupBy);
                }
            }
        }

        // Optional HAVING clause
        ISqlCondition? having = null;
        if (Match(SqlTokenType.Having))
        {
            having = ParseCondition();
        }

        // ORDER BY and LIMIT: only consume if not followed by UNION
        // (If UNION follows, ORDER BY belongs to the combined result, not this branch.)
        var orderBy = new List<SqlOrderByItem>();
        if (Match(SqlTokenType.Order))
        {
            Expect(SqlTokenType.By);
            orderBy.Add(ParseOrderByItem());

            while (Match(SqlTokenType.Comma))
            {
                var prevOrderBy = orderBy[^1];
                AttachTrailingComment(prevOrderBy);
                orderBy.Add(ParseOrderByItem());
            }

            var lastOrderBy = orderBy[^1];
            if (lastOrderBy.TrailingComment == null)
            {
                AttachTrailingComment(lastOrderBy);
            }
        }

        // Optional LIMIT clause (alternative to TOP)
        if (Match(SqlTokenType.Limit))
        {
            var limitToken = Expect(SqlTokenType.Number);
            top ??= int.Parse(limitToken.Value);
        }

        var statement = new SqlSelectStatement(
            columns,
            from,
            joins,
            where,
            orderBy,
            top,
            distinct,
            groupBy,
            having,
            groupByExpressions: groupByExpressions.Count > 0 ? groupByExpressions : null);
        statement.LeadingComments.AddRange(leadingComments);

        return statement;
    }

    /// <summary>
    /// Parses a UNION statement given that the first SELECT has already been parsed
    /// and the current token is UNION.
    /// </summary>
    private SqlUnionStatement ParseUnionStatement(SqlSelectStatement firstSelect)
    {
        var queries = new List<SqlSelectStatement> { firstSelect };
        var unionAllFlags = new List<bool>();
        var sourcePosition = firstSelect.SourcePosition;

        while (Match(SqlTokenType.Union))
        {
            var isAll = Match(SqlTokenType.All);
            unionAllFlags.Add(isAll);

            var nextSelect = ParseSelectStatementWithoutEndCheck();
            queries.Add(nextSelect);
        }

        // Optional trailing ORDER BY for the combined result
        List<SqlOrderByItem>? orderBy = null;
        if (Match(SqlTokenType.Order))
        {
            Expect(SqlTokenType.By);
            orderBy = new List<SqlOrderByItem>();
            orderBy.Add(ParseOrderByItem());

            while (Match(SqlTokenType.Comma))
            {
                var prevOrderBy = orderBy[^1];
                AttachTrailingComment(prevOrderBy);
                orderBy.Add(ParseOrderByItem());
            }

            var lastOrderBy = orderBy[^1];
            if (lastOrderBy.TrailingComment == null)
            {
                AttachTrailingComment(lastOrderBy);
            }
        }

        // Optional trailing LIMIT
        int? top = null;
        if (Match(SqlTokenType.Limit))
        {
            var limitToken = Expect(SqlTokenType.Number);
            top = int.Parse(limitToken.Value);
        }

        // Ensure we've consumed all tokens
        if (!IsAtEnd())
        {
            throw Error($"Unexpected token: {Peek().Value}");
        }

        return new SqlUnionStatement(queries, unionAllFlags, orderBy, top, sourcePosition);
    }

    /// <summary>
    /// Parses a SELECT statement used as a subquery (inside parentheses).
    /// Does not enforce end-of-input — the caller handles the closing paren.
    /// </summary>
    private SqlSelectStatement ParseSubquerySelectStatement()
    {
        Expect(SqlTokenType.Select);

        // Optional DISTINCT keyword
        var distinct = Match(SqlTokenType.Distinct);

        // Optional TOP clause
        int? top = null;
        if (Match(SqlTokenType.Top))
        {
            var topToken = Expect(SqlTokenType.Number);
            top = int.Parse(topToken.Value);
        }

        // SELECT columns (may include aggregates)
        var columns = ParseSelectColumnList();

        // FROM clause
        Expect(SqlTokenType.From);
        var from = ParseTableRef();
        AttachTrailingComment(from);

        // Optional JOIN clauses
        var joins = new List<SqlJoin>();
        while (MatchJoinKeyword())
        {
            var join = ParseJoin();
            AttachTrailingComment(join);
            joins.Add(join);
        }

        // Optional WHERE clause
        ISqlCondition? where = null;
        if (Match(SqlTokenType.Where))
        {
            where = ParseCondition();
        }

        // Optional GROUP BY clause
        var groupBy = new List<SqlColumnRef>();
        if (Match(SqlTokenType.Group))
        {
            Expect(SqlTokenType.By);
            groupBy.Add(ParseColumnRef());

            while (Match(SqlTokenType.Comma))
            {
                var prevGroupBy = groupBy[^1];
                AttachTrailingComment(prevGroupBy);
                groupBy.Add(ParseColumnRef());
            }

            var lastGroupBy = groupBy[^1];
            if (lastGroupBy.TrailingComment == null)
            {
                AttachTrailingComment(lastGroupBy);
            }
        }

        // Optional HAVING clause
        ISqlCondition? having = null;
        if (Match(SqlTokenType.Having))
        {
            having = ParseCondition();
        }

        // Optional ORDER BY clause
        var orderBy = new List<SqlOrderByItem>();
        if (Match(SqlTokenType.Order))
        {
            Expect(SqlTokenType.By);
            orderBy.Add(ParseOrderByItem());

            while (Match(SqlTokenType.Comma))
            {
                var prevOrderBy = orderBy[^1];
                AttachTrailingComment(prevOrderBy);
                orderBy.Add(ParseOrderByItem());
            }

            var lastOrderBy = orderBy[^1];
            if (lastOrderBy.TrailingComment == null)
            {
                AttachTrailingComment(lastOrderBy);
            }
        }

        // Optional LIMIT clause (alternative to TOP)
        if (Match(SqlTokenType.Limit))
        {
            var limitToken = Expect(SqlTokenType.Number);
            top ??= int.Parse(limitToken.Value);
        }

        // NOTE: No end-of-input check here — subquery stops at closing paren.

        return new SqlSelectStatement(
            columns,
            from,
            joins,
            where,
            orderBy,
            top,
            distinct,
            groupBy,
            having);
    }

    #endregion

    #region Variable Statement Parsing

    /// <summary>
    /// Parses a DECLARE statement:
    ///   DECLARE @name TYPE [= expression]
    /// </summary>
    private SqlDeclareStatement ParseDeclareStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Declare);

        var varToken = Expect(SqlTokenType.Variable);
        var variableName = varToken.Value;

        var typeName = ParseTypeName();

        ISqlExpression? initialValue = null;
        if (Match(SqlTokenType.Equals))
        {
            initialValue = ParseExpression();
        }

        return new SqlDeclareStatement(variableName, typeName, initialValue, sourcePosition);
    }

    /// <summary>
    /// Parses a SET @variable = expression statement.
    /// </summary>
    private SqlSetVariableStatement ParseSetVariableStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Set);

        var varToken = Expect(SqlTokenType.Variable);
        var variableName = varToken.Value;

        Expect(SqlTokenType.Equals);
        var value = ParseExpression();

        return new SqlSetVariableStatement(variableName, value, sourcePosition);
    }

    /// <summary>
    /// Parses an IF statement:
    ///   IF condition BEGIN ... END [ELSE BEGIN ... END]
    /// The condition is a standard SQL condition (comparison, logical, etc.).
    /// </summary>
    private SqlIfStatement ParseIfStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.If);

        var condition = ParseCondition();

        var thenBlock = ParseBlockStatement();

        SqlBlockStatement? elseBlock = null;
        if (Match(SqlTokenType.Else))
        {
            elseBlock = ParseBlockStatement();
        }

        return new SqlIfStatement(condition, thenBlock, elseBlock, sourcePosition);
    }

    /// <summary>
    /// Parses a BEGIN...END block containing a sequence of statements.
    ///   BEGIN statement [; statement ...] END
    /// Statements within the block may be separated by optional semicolons.
    /// </summary>
    private SqlBlockStatement ParseBlockStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Begin);

        var statements = new List<ISqlStatement>();
        SkipSemicolons();

        while (!Check(SqlTokenType.End) && !IsAtEnd())
        {
            statements.Add(ParseSingleStatementBody());
            SkipSemicolons();
        }

        Expect(SqlTokenType.End);

        return new SqlBlockStatement(statements, sourcePosition);
    }

    #endregion

    #region DML Parsing

    /// <summary>
    /// Parses an INSERT statement:
    ///   INSERT INTO entity (col1, col2, ...) VALUES (val1, val2, ...) [, (val1, val2, ...)]
    ///   INSERT INTO entity (col1, col2, ...) SELECT ...
    /// </summary>
    private SqlInsertStatement ParseInsertStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Insert);
        Expect(SqlTokenType.Into);

        var entityName = Expect(SqlTokenType.Identifier).Value;

        // Parse column list: ( identifier [, identifier]* )
        Expect(SqlTokenType.LeftParen);
        var columns = new List<string>();
        columns.Add(Expect(SqlTokenType.Identifier).Value);
        while (Match(SqlTokenType.Comma))
        {
            columns.Add(Expect(SqlTokenType.Identifier).Value);
        }
        Expect(SqlTokenType.RightParen);

        // VALUES or SELECT
        if (Check(SqlTokenType.Values))
        {
            Expect(SqlTokenType.Values);
            var valueRows = new List<IReadOnlyList<ISqlExpression>>();
            valueRows.Add(ParseValueRow());

            while (Match(SqlTokenType.Comma))
            {
                valueRows.Add(ParseValueRow());
            }

            // Validate column count matches value count
            foreach (var row in valueRows)
            {
                if (row.Count != columns.Count)
                {
                    throw Error(
                        $"VALUES row has {row.Count} values but {columns.Count} columns were specified");
                }
            }

            return new SqlInsertStatement(entityName, columns, valueRows, null, sourcePosition);
        }

        if (Check(SqlTokenType.Select))
        {
            var sourceQuery = ParseSelectStatementWithoutEndCheck();
            return new SqlInsertStatement(entityName, columns, null, sourceQuery, sourcePosition);
        }

        throw Error("Expected VALUES or SELECT after column list");
    }

    /// <summary>
    /// Parses a parenthesized value row: ( expression [, expression]* )
    /// </summary>
    private IReadOnlyList<ISqlExpression> ParseValueRow()
    {
        Expect(SqlTokenType.LeftParen);
        var values = new List<ISqlExpression>();
        values.Add(ParseExpression());

        while (Match(SqlTokenType.Comma))
        {
            values.Add(ParseExpression());
        }

        Expect(SqlTokenType.RightParen);
        return values;
    }

    /// <summary>
    /// Parses an UPDATE statement:
    ///   UPDATE table SET col = expr [, col = expr ...] [FROM table [JOIN ...]] WHERE ...
    /// </summary>
    private SqlUpdateStatement ParseUpdateStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Update);

        var targetTable = ParseTableRef();
        Expect(SqlTokenType.Set);

        // Parse SET clauses: identifier = expression [, identifier = expression]*
        var setClauses = new List<SqlSetClause>();
        setClauses.Add(ParseSetClause());

        while (Match(SqlTokenType.Comma))
        {
            setClauses.Add(ParseSetClause());
        }

        // Optional FROM clause for multi-table UPDATE
        SqlTableRef? fromTable = null;
        List<SqlJoin>? joins = null;
        if (Match(SqlTokenType.From))
        {
            fromTable = ParseTableRef();
            joins = new List<SqlJoin>();
            while (MatchJoinKeyword())
            {
                joins.Add(ParseJoin());
            }
        }

        // WHERE clause (required for safety)
        ISqlCondition? where = null;
        if (Match(SqlTokenType.Where))
        {
            where = ParseCondition();
        }

        if (where == null)
        {
            throw Error(
                "UPDATE without WHERE is not allowed. Add a WHERE clause to limit affected records.");
        }

        return new SqlUpdateStatement(targetTable, setClauses, where, fromTable, joins, sourcePosition);
    }

    /// <summary>
    /// Parses a single SET clause: column = expression.
    /// Supports both simple (column) and qualified (table.column) column names.
    /// </summary>
    private SqlSetClause ParseSetClause()
    {
        var first = Expect(SqlTokenType.Identifier).Value;

        // Handle qualified column: table.column = expression
        string columnName;
        if (Match(SqlTokenType.Dot))
        {
            // first was the table alias; the actual column is next
            columnName = Expect(SqlTokenType.Identifier).Value;
        }
        else
        {
            columnName = first;
        }

        Expect(SqlTokenType.Equals);
        var value = ParseExpression();
        return new SqlSetClause(columnName, value);
    }

    /// <summary>
    /// Parses a DELETE statement:
    ///   DELETE FROM entity [FROM table [JOIN ...]] WHERE ...
    /// </summary>
    private SqlDeleteStatement ParseDeleteStatement()
    {
        var sourcePosition = Peek().Position;
        Expect(SqlTokenType.Delete);
        Expect(SqlTokenType.From);

        var targetTable = ParseTableRef();

        // Optional secondary FROM clause for multi-table DELETE
        SqlTableRef? fromTable = null;
        List<SqlJoin>? joins = null;
        if (Match(SqlTokenType.From))
        {
            fromTable = ParseTableRef();
            joins = new List<SqlJoin>();
            while (MatchJoinKeyword())
            {
                joins.Add(ParseJoin());
            }
        }

        // WHERE clause (required for safety)
        ISqlCondition? where = null;
        if (Match(SqlTokenType.Where))
        {
            where = ParseCondition();
        }

        if (where == null)
        {
            throw Error(
                "DELETE without WHERE is not allowed. Use 'ppds truncate <entity>' for bulk deletion.");
        }

        return new SqlDeleteStatement(targetTable, where, fromTable, joins, sourcePosition);
    }

    #endregion

    #region Column Parsing

    /// <summary>
    /// Parses SELECT column list (may include aggregates).
    /// Tolerates trailing commas for better UX.
    /// </summary>
    private List<ISqlSelectColumn> ParseSelectColumnList()
    {
        var columns = new List<ISqlSelectColumn>();

        columns.Add(ParseSelectColumn());

        while (Match(SqlTokenType.Comma))
        {
            var prevColumn = columns[^1];
            AttachTrailingComment(prevColumn);

            // Check for trailing comma before FROM/WHERE/etc.
            if (IsAtClauseKeyword()) break;

            columns.Add(ParseSelectColumn());
        }

        var lastColumn = columns[^1];
        if (lastColumn.TrailingComment == null)
        {
            AttachTrailingComment(lastColumn);
        }

        return columns;
    }

    /// <summary>
    /// Parses a single SELECT column (regular column, aggregate function, window function, or computed expression).
    /// </summary>
    private ISqlSelectColumn ParseSelectColumn()
    {
        // Window ranking functions: ROW_NUMBER() OVER(...), RANK() OVER(...), DENSE_RANK() OVER(...)
        if (IsWindowRankingFunction())
        {
            return ParseWindowRankingColumn();
        }

        if (IsAggregateFunction())
        {
            // Look ahead: if aggregate is followed by '(' args ')' OVER, it's a window aggregate
            if (IsAggregateWindowFunction())
            {
                return ParseAggregateWindowColumn();
            }

            return ParseAggregateColumn();
        }

        // Star wildcard: only when * stands alone (not followed by an identifier/literal that could make it multiply)
        if (Check(SqlTokenType.Star))
        {
            // Look ahead: if next token (after *) is a clause keyword, comma, or EOF, it's a wildcard
            var next = PeekAt(_position + 1);
            if (next.Type == SqlTokenType.Eof ||
                next.Type == SqlTokenType.Comma ||
                next.Type == SqlTokenType.From ||
                next.Type == SqlTokenType.Where ||
                next.Type == SqlTokenType.Order ||
                next.Type == SqlTokenType.Group ||
                next.Type == SqlTokenType.Having ||
                next.Type == SqlTokenType.Limit ||
                next.Type == SqlTokenType.Join ||
                next.Type == SqlTokenType.Inner ||
                next.Type == SqlTokenType.Left ||
                next.Type == SqlTokenType.Right)
            {
                Advance(); // consume *
                return SqlColumnRef.Wildcard();
            }
        }

        // table.* wildcard: identifier.* pattern
        if (Check(SqlTokenType.Identifier) &&
            PeekAt(_position + 1).Type == SqlTokenType.Dot &&
            PeekAt(_position + 2).Type == SqlTokenType.Star)
        {
            // Check that * is not followed by an operand (i.e., it's a wildcard, not table.star * expr)
            var afterStar = PeekAt(_position + 3);
            if (afterStar.Type == SqlTokenType.Eof ||
                afterStar.Type == SqlTokenType.Comma ||
                afterStar.Type == SqlTokenType.From ||
                afterStar.Type == SqlTokenType.Where ||
                afterStar.Type == SqlTokenType.Order ||
                afterStar.Type == SqlTokenType.Group ||
                afterStar.Type == SqlTokenType.Having ||
                afterStar.Type == SqlTokenType.Limit ||
                afterStar.Type == SqlTokenType.Join ||
                afterStar.Type == SqlTokenType.Inner ||
                afterStar.Type == SqlTokenType.Left ||
                afterStar.Type == SqlTokenType.Right)
            {
                var tableName = Advance().Value; // consume identifier
                Advance(); // consume dot
                Advance(); // consume star
                return SqlColumnRef.Wildcard(tableName);
            }
        }

        // Parse as a full expression (handles arithmetic, CASE, IIF, literals, columns)
        var expression = ParseExpression();
        var alias = ParseOptionalAlias();

        // If the expression is a simple column reference with no operators, return SqlColumnRef
        if (expression is SqlColumnExpression colExpr)
        {
            var col = colExpr.Column;
            if (alias != null)
            {
                // Re-create with alias
                return col.TableName != null
                    ? SqlColumnRef.Qualified(col.TableName, col.ColumnName!, alias)
                    : SqlColumnRef.Simple(col.ColumnName!, alias);
            }
            return col;
        }

        // Any other expression (binary, unary, CASE, IIF, literal) → computed column
        return new SqlComputedColumn(expression, alias);
    }

    /// <summary>
    /// Checks if current token is an aggregate function.
    /// </summary>
    private bool IsAggregateFunction()
    {
        return Check(SqlTokenType.Count) ||
               Check(SqlTokenType.Sum) ||
               Check(SqlTokenType.Avg) ||
               Check(SqlTokenType.Min) ||
               Check(SqlTokenType.Max);
    }

    /// <summary>
    /// Checks if current token is a window ranking function (ROW_NUMBER, RANK, DENSE_RANK).
    /// </summary>
    private bool IsWindowRankingFunction()
    {
        return Check(SqlTokenType.RowNumber) ||
               Check(SqlTokenType.Rank) ||
               Check(SqlTokenType.DenseRank);
    }

    /// <summary>
    /// Checks if the current aggregate function is followed by OVER (making it a window aggregate).
    /// Scans ahead: aggregate '(' ... ')' OVER.
    /// </summary>
    private bool IsAggregateWindowFunction()
    {
        // We know current token is an aggregate function token.
        // Look ahead to find the matching ')' after '(' and check if OVER follows.
        var scanPos = _position + 1; // skip the aggregate token
        if (scanPos >= _tokens.Count || _tokens[scanPos].Type != SqlTokenType.LeftParen)
        {
            return false;
        }

        // Find matching right paren
        var depth = 0;
        for (var i = scanPos; i < _tokens.Count; i++)
        {
            if (_tokens[i].Type == SqlTokenType.LeftParen)
            {
                depth++;
            }
            else if (_tokens[i].Type == SqlTokenType.RightParen)
            {
                depth--;
                if (depth == 0)
                {
                    // Check if the token after ')' is OVER
                    var afterParen = i + 1;
                    return afterParen < _tokens.Count && _tokens[afterParen].Type == SqlTokenType.Over;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a window ranking function: ROW_NUMBER() OVER (...), RANK() OVER (...), DENSE_RANK() OVER (...).
    /// </summary>
    private SqlComputedColumn ParseWindowRankingColumn()
    {
        var funcToken = Advance(); // consume ROW_NUMBER, RANK, or DENSE_RANK
        var functionName = funcToken.Type switch
        {
            SqlTokenType.RowNumber => "ROW_NUMBER",
            SqlTokenType.Rank => "RANK",
            SqlTokenType.DenseRank => "DENSE_RANK",
            _ => throw Error($"Unexpected window function: {funcToken.Type}")
        };

        Expect(SqlTokenType.LeftParen);
        Expect(SqlTokenType.RightParen);

        var (partitionBy, orderBy) = ParseOverClause();

        var windowExpr = new SqlWindowExpression(functionName, null, partitionBy, orderBy);
        var alias = ParseOptionalAlias();
        return new SqlComputedColumn(windowExpr, alias);
    }

    /// <summary>
    /// Parses an aggregate window function: SUM(revenue) OVER (...), COUNT(*) OVER (...), etc.
    /// The aggregate token is at current position and we already verified OVER follows the closing paren.
    /// </summary>
    private SqlComputedColumn ParseAggregateWindowColumn()
    {
        var funcToken = Advance(); // consume aggregate keyword
        var functionName = funcToken.Value.ToUpperInvariant();

        Expect(SqlTokenType.LeftParen);

        ISqlExpression? operand = null;
        var isCountStar = false;

        if (funcToken.Type == SqlTokenType.Count && Check(SqlTokenType.Star))
        {
            Advance(); // consume *
            isCountStar = true;
        }
        else if (!Check(SqlTokenType.RightParen))
        {
            operand = ParseExpression();
        }

        Expect(SqlTokenType.RightParen);

        var (partitionBy, orderBy) = ParseOverClause();

        var windowExpr = new SqlWindowExpression(functionName, operand, partitionBy, orderBy, isCountStar);
        var alias = ParseOptionalAlias();
        return new SqlComputedColumn(windowExpr, alias);
    }

    /// <summary>
    /// Parses OVER ([PARTITION BY expr, ...] [ORDER BY col [ASC|DESC], ...]).
    /// The OVER keyword is expected at the current position.
    /// </summary>
    private (IReadOnlyList<ISqlExpression>? partitionBy, IReadOnlyList<SqlOrderByItem>? orderBy) ParseOverClause()
    {
        Expect(SqlTokenType.Over);
        Expect(SqlTokenType.LeftParen);

        List<ISqlExpression>? partitionBy = null;
        List<SqlOrderByItem>? orderBy = null;

        // PARTITION BY
        if (Check(SqlTokenType.Partition))
        {
            Advance(); // consume PARTITION
            Expect(SqlTokenType.By);

            partitionBy = new List<ISqlExpression>();
            partitionBy.Add(ParseExpression());

            while (Match(SqlTokenType.Comma))
            {
                partitionBy.Add(ParseExpression());
            }
        }

        // ORDER BY
        if (Check(SqlTokenType.Order))
        {
            Advance(); // consume ORDER
            Expect(SqlTokenType.By);

            orderBy = new List<SqlOrderByItem>();
            orderBy.Add(ParseOrderByItem());

            while (Match(SqlTokenType.Comma))
            {
                orderBy.Add(ParseOrderByItem());
            }
        }

        Expect(SqlTokenType.RightParen);

        return (partitionBy, orderBy);
    }

    /// <summary>
    /// Parses an aggregate function: COUNT(*), COUNT(column), SUM(column), etc.
    /// </summary>
    private SqlAggregateColumn ParseAggregateColumn()
    {
        var funcToken = Advance();
        var func = funcToken.Type switch
        {
            SqlTokenType.Count => SqlAggregateFunction.Count,
            SqlTokenType.Sum => SqlAggregateFunction.Sum,
            SqlTokenType.Avg => SqlAggregateFunction.Avg,
            SqlTokenType.Min => SqlAggregateFunction.Min,
            SqlTokenType.Max => SqlAggregateFunction.Max,
            _ => throw Error($"Unexpected aggregate function: {funcToken.Type}")
        };

        Expect(SqlTokenType.LeftParen);

        SqlColumnRef? column = null;
        var isDistinct = false;

        if (func == SqlAggregateFunction.Count && Match(SqlTokenType.Star))
        {
            // COUNT(*)
            column = null;
        }
        else
        {
            // Check for DISTINCT inside aggregate: COUNT(DISTINCT column)
            isDistinct = Match(SqlTokenType.Distinct);
            column = ParseColumnRef();
        }

        Expect(SqlTokenType.RightParen);

        var alias = ParseOptionalAlias();

        return new SqlAggregateColumn(func, column, isDistinct, alias);
    }

    /// <summary>
    /// Checks if current token is a SQL clause keyword.
    /// </summary>
    private bool IsAtClauseKeyword()
    {
        return Check(SqlTokenType.From) ||
               Check(SqlTokenType.Where) ||
               Check(SqlTokenType.Group) ||
               Check(SqlTokenType.Having) ||
               Check(SqlTokenType.Order) ||
               Check(SqlTokenType.Limit) ||
               Check(SqlTokenType.Join) ||
               Check(SqlTokenType.Left) ||
               Check(SqlTokenType.Right) ||
               Check(SqlTokenType.Inner);
    }

    /// <summary>
    /// Parses a single column reference.
    /// </summary>
    private SqlColumnRef ParseColumnRef()
    {
        // Check for *
        if (Match(SqlTokenType.Star))
        {
            return SqlColumnRef.Wildcard();
        }

        // Parse identifier (might be table.column or just column)
        var first = Expect(SqlTokenType.Identifier);

        // Check for table.column or table.*
        if (Match(SqlTokenType.Dot))
        {
            if (Match(SqlTokenType.Star))
            {
                return SqlColumnRef.Wildcard(first.Value);
            }
            var column = Expect(SqlTokenType.Identifier);
            var alias = ParseOptionalAlias();
            return SqlColumnRef.Qualified(first.Value, column.Value, alias);
        }

        // Just a column name
        var colAlias = ParseOptionalAlias();
        return SqlColumnRef.Simple(first.Value, colAlias);
    }

    /// <summary>
    /// Parses optional AS alias or just alias.
    /// </summary>
    private string? ParseOptionalAlias()
    {
        if (Match(SqlTokenType.As))
        {
            // After AS, accept identifier or keyword as alias
            var token = Peek();
            if (token.Type == SqlTokenType.Identifier || token.Type.IsKeyword())
            {
                return Advance().Value;
            }
            throw Error($"Expected alias after AS, found {token.Type}");
        }

        // Check for alias without AS keyword - must be an identifier (not keyword)
        if (Check(SqlTokenType.Identifier) && !CheckKeyword())
        {
            return Advance().Value;
        }

        return null;
    }

    /// <summary>
    /// Parses a single GROUP BY item. If the item is an identifier followed by '(',
    /// it is a function expression (e.g., YEAR(createdon)) added to groupByExpressions.
    /// Otherwise it is a plain column reference added to groupBy.
    /// </summary>
    private void ParseGroupByItem(List<SqlColumnRef> groupBy, List<ISqlExpression> groupByExpressions)
    {
        // Check for function expression: identifier followed by '('
        if (Check(SqlTokenType.Identifier) && PeekAt(_position + 1).Type == SqlTokenType.LeftParen)
        {
            var funcName = Advance().Value;
            var funcExpr = ParseFunctionCall(funcName);
            groupByExpressions.Add(funcExpr);
            return;
        }

        groupBy.Add(ParseColumnRef());
    }

    #endregion

    #region Table and Join Parsing

    /// <summary>
    /// Parses a table reference.
    /// </summary>
    private SqlTableRef ParseTableRef()
    {
        var tableName = Expect(SqlTokenType.Identifier);
        var alias = ParseOptionalAlias();
        return new SqlTableRef(tableName.Value, alias);
    }

    /// <summary>
    /// Checks if current token starts a JOIN clause.
    /// </summary>
    private bool MatchJoinKeyword()
    {
        return Check(SqlTokenType.Join) ||
               Check(SqlTokenType.Inner) ||
               Check(SqlTokenType.Left) ||
               Check(SqlTokenType.Right);
    }

    /// <summary>
    /// Parses a JOIN clause.
    /// </summary>
    private SqlJoin ParseJoin()
    {
        var joinType = SqlJoinType.Inner;

        if (Match(SqlTokenType.Inner))
        {
            joinType = SqlJoinType.Inner;
        }
        else if (Match(SqlTokenType.Left))
        {
            Match(SqlTokenType.Outer); // optional
            joinType = SqlJoinType.Left;
        }
        else if (Match(SqlTokenType.Right))
        {
            Match(SqlTokenType.Outer); // optional
            joinType = SqlJoinType.Right;
        }

        Expect(SqlTokenType.Join);
        var table = ParseTableRef();
        Expect(SqlTokenType.On);

        var leftColumn = ParseColumnRef();
        Expect(SqlTokenType.Equals);
        var rightColumn = ParseColumnRef();

        return new SqlJoin(joinType, table, leftColumn, rightColumn);
    }

    #endregion

    #region Expression Parsing

    /// <summary>
    /// Parses an expression with full operator precedence:
    /// additive → multiplicative → unary → primary.
    /// </summary>
    private ISqlExpression ParseExpression()
    {
        return ParseAdditiveExpression();
    }

    /// <summary>
    /// Parses additive expressions: multiplicative (('+' | '-') multiplicative)*.
    /// </summary>
    private ISqlExpression ParseAdditiveExpression()
    {
        var left = ParseMultiplicativeExpression();

        while (Check(SqlTokenType.Plus) || Check(SqlTokenType.Minus))
        {
            var opToken = Advance();
            var op = opToken.Type == SqlTokenType.Plus
                ? SqlBinaryOperator.Add
                : SqlBinaryOperator.Subtract;
            var right = ParseMultiplicativeExpression();
            left = new SqlBinaryExpression(left, op, right);
        }

        return left;
    }

    /// <summary>
    /// Parses multiplicative expressions: unary (('*' | '/' | '%') unary)*.
    /// Note: Star token is reused for multiply in expression context.
    /// </summary>
    private ISqlExpression ParseMultiplicativeExpression()
    {
        var left = ParseUnaryExpression();

        while (Check(SqlTokenType.Star) || Check(SqlTokenType.Slash) || Check(SqlTokenType.Percent))
        {
            var opToken = Advance();
            var op = opToken.Type switch
            {
                SqlTokenType.Star => SqlBinaryOperator.Multiply,
                SqlTokenType.Slash => SqlBinaryOperator.Divide,
                SqlTokenType.Percent => SqlBinaryOperator.Modulo,
                _ => throw Error($"Unexpected operator: {opToken.Type}")
            };
            var right = ParseUnaryExpression();
            left = new SqlBinaryExpression(left, op, right);
        }

        return left;
    }

    /// <summary>
    /// Parses unary expressions: ['-'] primary.
    /// Folds -number into a negative literal for simpler downstream handling.
    /// </summary>
    private ISqlExpression ParseUnaryExpression()
    {
        if (Match(SqlTokenType.Minus))
        {
            var operand = ParsePrimaryExpression();

            // Constant folding: -number → negative literal
            if (operand is SqlLiteralExpression lit && lit.Value.Type == SqlLiteralType.Number)
            {
                return new SqlLiteralExpression(SqlLiteral.Number("-" + lit.Value.Value));
            }

            return new SqlUnaryExpression(SqlUnaryOperator.Negate, operand);
        }

        return ParsePrimaryExpression();
    }

    /// <summary>
    /// Parses primary expressions: literal, column reference, CASE, IIF, CAST, CONVERT, or parenthesized expression.
    /// </summary>
    private ISqlExpression ParsePrimaryExpression()
    {
        if (Check(SqlTokenType.Case))
        {
            return ParseCaseExpression();
        }

        if (Check(SqlTokenType.Iif))
        {
            return ParseIifExpression();
        }

        if (Check(SqlTokenType.Cast))
        {
            return ParseCastExpression();
        }

        if (Check(SqlTokenType.Convert))
        {
            return ParseConvertExpression();
        }

        if (Match(SqlTokenType.LeftParen))
        {
            var inner = ParseExpression();
            Expect(SqlTokenType.RightParen);
            return inner;
        }

        if (Check(SqlTokenType.String))
        {
            Advance();
            return new SqlLiteralExpression(SqlLiteral.String(Previous().Value));
        }

        if (Check(SqlTokenType.Number))
        {
            Advance();
            return new SqlLiteralExpression(SqlLiteral.Number(Previous().Value));
        }

        if (Check(SqlTokenType.Null))
        {
            Advance();
            return new SqlLiteralExpression(SqlLiteral.Null());
        }

        // Variable reference: @name
        if (Check(SqlTokenType.Variable))
        {
            var varToken = Advance();
            return new SqlVariableExpression(varToken.Value);
        }

        // Column reference or function call (identifier, possibly table.column or func(...))
        if (Check(SqlTokenType.Identifier))
        {
            var first = Advance();

            // Function call: identifier followed by '('
            if (Check(SqlTokenType.LeftParen))
            {
                return ParseFunctionCall(first.Value);
            }

            if (Match(SqlTokenType.Dot))
            {
                var second = Expect(SqlTokenType.Identifier);
                return new SqlColumnExpression(SqlColumnRef.Qualified(first.Value, second.Value));
            }
            return new SqlColumnExpression(SqlColumnRef.Simple(first.Value));
        }

        throw Error($"Expected expression, found {Peek().Type}");
    }

    /// <summary>
    /// Parses a function call: name(arg1, arg2, ...).
    /// The function name has already been consumed; the current token is '('.
    /// Date function datepart arguments (year, month, day, etc.) are unquoted
    /// identifiers in T-SQL, so they are parsed as identifier-valued expressions
    /// wrapped in SqlLiteralExpression with string type.
    /// </summary>
    private SqlFunctionExpression ParseFunctionCall(string functionName)
    {
        Expect(SqlTokenType.LeftParen);

        var args = new List<ISqlExpression>();

        // Handle zero-argument functions like GETDATE()
        if (!Check(SqlTokenType.RightParen))
        {
            args.Add(ParseFunctionArgument());

            while (Match(SqlTokenType.Comma))
            {
                args.Add(ParseFunctionArgument());
            }
        }

        Expect(SqlTokenType.RightParen);

        return new SqlFunctionExpression(functionName, args);
    }

    /// <summary>
    /// Parses a single function argument. Handles the T-SQL convention where
    /// datepart arguments (year, month, day, hour, minute, second, quarter, week,
    /// dayofyear) are unquoted identifiers — not column references.
    /// </summary>
    private ISqlExpression ParseFunctionArgument()
    {
        // Check for datepart-style keyword: unquoted identifier that is a known datepart.
        // These must not be confused with column references. Peek ahead: if it's an
        // identifier followed by a comma or right-paren, and it's a known datepart name,
        // treat it as a string literal.
        if (Check(SqlTokenType.Identifier))
        {
            var name = Peek().Value;
            if (IsDatePart(name))
            {
                var nextAfter = PeekAt(_position + 1);
                if (nextAfter.Type == SqlTokenType.Comma || nextAfter.Type == SqlTokenType.RightParen)
                {
                    Advance();
                    return new SqlLiteralExpression(SqlLiteral.String(name.ToLowerInvariant()));
                }
            }
        }

        return ParseExpression();
    }

    /// <summary>
    /// Checks whether a name is a T-SQL datepart keyword used by date functions
    /// (DATEADD, DATEDIFF, DATEPART, DATETRUNC).
    /// </summary>
    private static bool IsDatePart(string name)
    {
        return name.Equals("year", StringComparison.OrdinalIgnoreCase)
            || name.Equals("yy", StringComparison.OrdinalIgnoreCase)
            || name.Equals("yyyy", StringComparison.OrdinalIgnoreCase)
            || name.Equals("quarter", StringComparison.OrdinalIgnoreCase)
            || name.Equals("qq", StringComparison.OrdinalIgnoreCase)
            || name.Equals("q", StringComparison.OrdinalIgnoreCase)
            || name.Equals("month", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("m", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dayofyear", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dy", StringComparison.OrdinalIgnoreCase)
            || name.Equals("y", StringComparison.OrdinalIgnoreCase)
            || name.Equals("day", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("d", StringComparison.OrdinalIgnoreCase)
            || name.Equals("week", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wk", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ww", StringComparison.OrdinalIgnoreCase)
            || name.Equals("hour", StringComparison.OrdinalIgnoreCase)
            || name.Equals("hh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("minute", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mi", StringComparison.OrdinalIgnoreCase)
            || name.Equals("n", StringComparison.OrdinalIgnoreCase)
            || name.Equals("second", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ss", StringComparison.OrdinalIgnoreCase)
            || name.Equals("s", StringComparison.OrdinalIgnoreCase)
            || name.Equals("millisecond", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ms", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses CAST(expression AS type_name).
    /// </summary>
    private SqlCastExpression ParseCastExpression()
    {
        Expect(SqlTokenType.Cast);
        Expect(SqlTokenType.LeftParen);

        var expression = ParseExpression();
        Expect(SqlTokenType.As);
        var typeName = ParseTypeName();

        Expect(SqlTokenType.RightParen);

        return new SqlCastExpression(expression, typeName);
    }

    /// <summary>
    /// Parses CONVERT(type_name, expression [, style]).
    /// </summary>
    private SqlCastExpression ParseConvertExpression()
    {
        Expect(SqlTokenType.Convert);
        Expect(SqlTokenType.LeftParen);

        var typeName = ParseTypeName();
        Expect(SqlTokenType.Comma);
        var expression = ParseExpression();

        int? style = null;
        if (Match(SqlTokenType.Comma))
        {
            var styleToken = Expect(SqlTokenType.Number);
            style = int.Parse(styleToken.Value);
        }

        Expect(SqlTokenType.RightParen);

        return new SqlCastExpression(expression, typeName, style);
    }

    /// <summary>
    /// Parses a SQL type name, including parameterized types like nvarchar(100) or decimal(18,2).
    /// Accepts identifiers and keywords (e.g., "date" is a keyword-like identifier).
    /// </summary>
    private string ParseTypeName()
    {
        // Type name can be an identifier or a keyword used as type (e.g., "float", "date")
        string typeName;
        if (Check(SqlTokenType.Identifier))
        {
            typeName = Advance().Value;
        }
        else if (Peek().Type.IsKeyword())
        {
            // Allow keywords to be used as type names (e.g., "float", "date")
            typeName = Advance().Value;
        }
        else
        {
            throw Error($"Expected type name, found {Peek().Type}");
        }

        // Check for parameterized type: type(params)
        if (Match(SqlTokenType.LeftParen))
        {
            var paramBuilder = new System.Text.StringBuilder();
            paramBuilder.Append(typeName);
            paramBuilder.Append('(');

            // Read first parameter
            var firstParam = Expect(SqlTokenType.Number);
            paramBuilder.Append(firstParam.Value);

            // Optional second parameter: decimal(18,2)
            if (Match(SqlTokenType.Comma))
            {
                var secondParam = Expect(SqlTokenType.Number);
                paramBuilder.Append(',');
                paramBuilder.Append(secondParam.Value);
            }

            Expect(SqlTokenType.RightParen);
            paramBuilder.Append(')');
            return paramBuilder.ToString();
        }

        return typeName;
    }

    /// <summary>
    /// Parses a CASE WHEN condition THEN expression [WHEN ...] [ELSE expression] END.
    /// </summary>
    private SqlCaseExpression ParseCaseExpression()
    {
        Expect(SqlTokenType.Case);

        var whenClauses = new List<SqlWhenClause>();

        // At least one WHEN clause required
        do
        {
            Expect(SqlTokenType.When);
            var condition = ParseCondition();
            Expect(SqlTokenType.Then);
            var result = ParseExpression();
            whenClauses.Add(new SqlWhenClause(condition, result));
        }
        while (Check(SqlTokenType.When));

        // Optional ELSE clause
        ISqlExpression? elseExpression = null;
        if (Match(SqlTokenType.Else))
        {
            elseExpression = ParseExpression();
        }

        Expect(SqlTokenType.End);

        return new SqlCaseExpression(whenClauses, elseExpression);
    }

    /// <summary>
    /// Parses IIF(condition, true_value, false_value).
    /// </summary>
    private SqlIifExpression ParseIifExpression()
    {
        Expect(SqlTokenType.Iif);
        Expect(SqlTokenType.LeftParen);

        var condition = ParseCondition();
        Expect(SqlTokenType.Comma);
        var trueValue = ParseExpression();
        Expect(SqlTokenType.Comma);
        var falseValue = ParseExpression();

        Expect(SqlTokenType.RightParen);

        return new SqlIifExpression(condition, trueValue, falseValue);
    }

    #endregion

    #region Condition Parsing

    /// <summary>
    /// Parses a WHERE condition (handles AND/OR precedence).
    /// </summary>
    private ISqlCondition ParseCondition()
    {
        return ParseOrCondition();
    }

    /// <summary>
    /// Parses OR conditions (lowest precedence).
    /// </summary>
    private ISqlCondition ParseOrCondition()
    {
        var left = ParseAndCondition();

        while (Match(SqlTokenType.Or))
        {
            var right = ParseAndCondition();
            left = SqlLogicalCondition.Or(left, right);
        }

        return left;
    }

    /// <summary>
    /// Parses AND conditions (higher precedence than OR).
    /// </summary>
    private ISqlCondition ParseAndCondition()
    {
        var left = ParsePrimaryCondition();

        while (Match(SqlTokenType.And))
        {
            var right = ParsePrimaryCondition();
            left = SqlLogicalCondition.And(left, right);
        }

        return left;
    }

    /// <summary>
    /// Parses primary conditions (comparison, LIKE, IS NULL, IN, EXISTS, or parenthesized).
    /// </summary>
    private ISqlCondition ParsePrimaryCondition()
    {
        // EXISTS (SELECT ...)
        if (Check(SqlTokenType.Exists))
        {
            return ParseExistsCondition(false);
        }

        // NOT EXISTS (SELECT ...)
        if (Check(SqlTokenType.Not) && PeekAt(_position + 1).Type == SqlTokenType.Exists)
        {
            Advance(); // consume NOT
            return ParseExistsCondition(true);
        }

        // Parenthesized condition
        if (Match(SqlTokenType.LeftParen))
        {
            var condition = ParseCondition();
            Expect(SqlTokenType.RightParen);
            AttachTrailingComment(condition);
            return condition;
        }

        // Variable or numeric literal on left side of condition (e.g., IF @count > 0)
        if (Check(SqlTokenType.Variable) || Check(SqlTokenType.Number) || Check(SqlTokenType.String))
        {
            var leftExpr = ParseExpression();
            var op = ParseComparisonOperator();
            var rightExpr = ParseExpression();

            if (leftExpr is SqlColumnExpression colExpr2 && rightExpr is SqlLiteralExpression litExpr2)
            {
                var compCond = new SqlComparisonCondition(colExpr2.Column, op, litExpr2.Value);
                AttachTrailingComment(compCond);
                return compCond;
            }

            var exprCond2 = new SqlExpressionCondition(leftExpr, op, rightExpr);
            AttachTrailingComment(exprCond2);
            return exprCond2;
        }

        // Column-based condition
        var column = ParseColumnRef();

        // IS [NOT] NULL
        if (Match(SqlTokenType.Is))
        {
            var isNegated = Match(SqlTokenType.Not);
            Expect(SqlTokenType.Null);
            var cond = new SqlNullCondition(column, isNegated);
            AttachTrailingComment(cond);
            return cond;
        }

        // [NOT] LIKE
        var likeNegated = Match(SqlTokenType.Not);
        if (Match(SqlTokenType.Like))
        {
            var pattern = Expect(SqlTokenType.String);
            var cond = new SqlLikeCondition(column, pattern.Value, likeNegated);
            AttachTrailingComment(cond);
            return cond;
        }

        if (likeNegated)
        {
            // NOT was consumed but no LIKE followed
            // Check for NOT IN
            if (Match(SqlTokenType.In))
            {
                var cond = ParseInList(column, true);
                AttachTrailingComment(cond);
                return cond;
            }
            // Check for NOT BETWEEN
            if (Match(SqlTokenType.Between))
            {
                var cond = ParseBetween(column, true);
                AttachTrailingComment(cond);
                return cond;
            }
            throw Error("Expected LIKE, IN, or BETWEEN after NOT");
        }

        // [NOT] IN
        if (Match(SqlTokenType.In))
        {
            var cond = ParseInList(column, false);
            AttachTrailingComment(cond);
            return cond;
        }

        // BETWEEN low AND high → col >= low AND col <= high
        if (Match(SqlTokenType.Between))
        {
            var cond = ParseBetween(column, false);
            AttachTrailingComment(cond);
            return cond;
        }

        // Comparison operator: parse right side as expression to support
        // column-to-column (WHERE revenue > cost) and computed conditions
        // (WHERE revenue * 0.1 > 100). If right side is a simple literal,
        // produce SqlComparisonCondition for FetchXML pushdown compatibility.
        var op2 = ParseComparisonOperator();
        var rightExpr2 = ParseExpression();

        if (rightExpr2 is SqlLiteralExpression litExpr)
        {
            // Simple column op literal: backward-compatible SqlComparisonCondition
            var compCond = new SqlComparisonCondition(column, op2, litExpr.Value);
            AttachTrailingComment(compCond);
            return compCond;
        }
        else
        {
            // Expression on right side (column, arithmetic, etc.): use SqlExpressionCondition
            var leftExpr2 = new SqlColumnExpression(column);
            var exprCond = new SqlExpressionCondition(leftExpr2, op2, rightExpr2);
            AttachTrailingComment(exprCond);
            return exprCond;
        }
    }

    /// <summary>
    /// Parses IN (value1, value2, ...) list or IN (SELECT ...) subquery.
    /// </summary>
    private ISqlCondition ParseInList(SqlColumnRef column, bool isNegated)
    {
        Expect(SqlTokenType.LeftParen);

        // Check if this is a subquery: IN (SELECT ...)
        if (Check(SqlTokenType.Select))
        {
            var subquery = ParseSubquerySelectStatement();
            Expect(SqlTokenType.RightParen);
            return new SqlInSubqueryCondition(column, subquery, isNegated);
        }

        var values = new List<SqlLiteral>();

        do
        {
            values.Add(ParseLiteral());
        } while (Match(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);
        return new SqlInCondition(column, values, isNegated);
    }

    /// <summary>
    /// Parses EXISTS (SELECT ...) or NOT EXISTS (SELECT ...).
    /// The NOT token has already been consumed by the caller if isNegated is true.
    /// </summary>
    private SqlExistsCondition ParseExistsCondition(bool isNegated)
    {
        Expect(SqlTokenType.Exists);
        Expect(SqlTokenType.LeftParen);
        var subquery = ParseSubquerySelectStatement();
        Expect(SqlTokenType.RightParen);

        var cond = new SqlExistsCondition(subquery, isNegated);
        AttachTrailingComment(cond);
        return cond;
    }

    /// <summary>
    /// Parses BETWEEN low AND high (or NOT BETWEEN).
    /// Desugars to: column &gt;= low AND column &lt;= high
    /// (or NOT (column &gt;= low AND column &lt;= high) for NOT BETWEEN).
    /// The BETWEEN token has already been consumed.
    /// </summary>
    private ISqlCondition ParseBetween(SqlColumnRef column, bool isNegated)
    {
        var lowExpr = ParseExpression();
        Expect(SqlTokenType.And);
        var highExpr = ParseExpression();

        // Desugar to: column >= low AND column <= high
        ISqlCondition geCond;
        ISqlCondition leCond;

        if (lowExpr is SqlLiteralExpression lowLit)
        {
            geCond = new SqlComparisonCondition(column, SqlComparisonOperator.GreaterThanOrEqual, lowLit.Value);
        }
        else
        {
            geCond = new SqlExpressionCondition(
                new SqlColumnExpression(column), SqlComparisonOperator.GreaterThanOrEqual, lowExpr);
        }

        if (highExpr is SqlLiteralExpression highLit)
        {
            leCond = new SqlComparisonCondition(column, SqlComparisonOperator.LessThanOrEqual, highLit.Value);
        }
        else
        {
            leCond = new SqlExpressionCondition(
                new SqlColumnExpression(column), SqlComparisonOperator.LessThanOrEqual, highExpr);
        }

        ISqlCondition result = SqlLogicalCondition.And(geCond, leCond);

        // NOT BETWEEN → negate the whole thing
        // NOT (col >= low AND col <= high) is equivalent to col < low OR col > high
        // But for simplicity, we keep the AND and let the caller interpret NOT BETWEEN.
        // Actually, since we desugar, NOT BETWEEN x AND y = col < x OR col > y.
        if (isNegated)
        {
            // Desugar NOT BETWEEN to: column < low OR column > high
            ISqlCondition ltCond;
            ISqlCondition gtCond;

            if (lowExpr is SqlLiteralExpression lowLit2)
            {
                ltCond = new SqlComparisonCondition(column, SqlComparisonOperator.LessThan, lowLit2.Value);
            }
            else
            {
                ltCond = new SqlExpressionCondition(
                    new SqlColumnExpression(column), SqlComparisonOperator.LessThan, lowExpr);
            }

            if (highExpr is SqlLiteralExpression highLit2)
            {
                gtCond = new SqlComparisonCondition(column, SqlComparisonOperator.GreaterThan, highLit2.Value);
            }
            else
            {
                gtCond = new SqlExpressionCondition(
                    new SqlColumnExpression(column), SqlComparisonOperator.GreaterThan, highExpr);
            }

            result = SqlLogicalCondition.Or(ltCond, gtCond);
        }

        return result;
    }

    /// <summary>
    /// Parses a comparison operator.
    /// </summary>
    private SqlComparisonOperator ParseComparisonOperator()
    {
        if (Match(SqlTokenType.Equals)) return SqlComparisonOperator.Equal;
        if (Match(SqlTokenType.NotEquals)) return SqlComparisonOperator.NotEqual;
        if (Match(SqlTokenType.LessThan)) return SqlComparisonOperator.LessThan;
        if (Match(SqlTokenType.GreaterThan)) return SqlComparisonOperator.GreaterThan;
        if (Match(SqlTokenType.LessThanOrEqual)) return SqlComparisonOperator.LessThanOrEqual;
        if (Match(SqlTokenType.GreaterThanOrEqual)) return SqlComparisonOperator.GreaterThanOrEqual;

        throw Error("Expected comparison operator");
    }

    /// <summary>
    /// Parses a literal value. Handles negative numbers (Minus followed by Number).
    /// </summary>
    private SqlLiteral ParseLiteral()
    {
        if (Match(SqlTokenType.String))
        {
            return SqlLiteral.String(Previous().Value);
        }
        if (Match(SqlTokenType.Minus))
        {
            var num = Expect(SqlTokenType.Number);
            return SqlLiteral.Number("-" + num.Value);
        }
        if (Match(SqlTokenType.Number))
        {
            return SqlLiteral.Number(Previous().Value);
        }
        if (Match(SqlTokenType.Null))
        {
            return SqlLiteral.Null();
        }

        throw Error("Expected literal value");
    }

    #endregion

    #region Order By Parsing

    /// <summary>
    /// Parses an ORDER BY item.
    /// </summary>
    private SqlOrderByItem ParseOrderByItem()
    {
        var column = ParseColumnRef();
        var direction = SqlSortDirection.Ascending;

        if (Match(SqlTokenType.Desc))
        {
            direction = SqlSortDirection.Descending;
        }
        else
        {
            Match(SqlTokenType.Asc); // optional
        }

        return new SqlOrderByItem(column, direction);
    }

    #endregion

    #region Token Helpers

    private SqlToken Peek() =>
        _position < _tokens.Count ? _tokens[_position] : new SqlToken(SqlTokenType.Eof, "", _sql.Length);

    private SqlToken PeekAt(int index) =>
        index < _tokens.Count ? _tokens[index] : new SqlToken(SqlTokenType.Eof, "", _sql.Length);

    private SqlToken Previous()
    {
        if (_position <= 0)
        {
            throw new InvalidOperationException("No previous token available");
        }
        return _tokens[_position - 1];
    }

    private bool IsAtEnd() => Peek().Type == SqlTokenType.Eof;

    private bool Check(SqlTokenType type) => Peek().Type == type;

    private bool CheckKeyword() => Peek().Type.IsKeyword();

    private SqlToken Advance()
    {
        if (!IsAtEnd())
        {
            _position++;
        }
        return Previous();
    }

    private bool Match(SqlTokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }
        return false;
    }

    private SqlToken Expect(SqlTokenType type)
    {
        if (Check(type))
        {
            return Advance();
        }
        throw Error($"Expected {type}, found {Peek().Type}");
    }

    private SqlParseException Error(string message)
    {
        var token = Peek();
        return SqlParseException.AtPosition(message, token.Position, _sql);
    }

    #endregion
}
