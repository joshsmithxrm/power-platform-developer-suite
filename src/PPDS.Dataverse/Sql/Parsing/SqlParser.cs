using System;
using System.Collections.Generic;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Recursive descent parser for a subset of SQL SELECT statements.
/// Produces a SqlSelectStatement AST from SQL text.
/// </summary>
/// <remarks>
/// Supported SQL:
/// - SELECT columns FROM table
/// - SELECT DISTINCT columns FROM table
/// - SELECT TOP n columns FROM table
/// - Aggregate functions: COUNT(*), COUNT(column), SUM, AVG, MIN, MAX
/// - COUNT(DISTINCT column)
/// - GROUP BY column1, column2
/// - WHERE with comparison, LIKE, IS NULL, IN operators
/// - AND/OR logical operators with parentheses
/// - ORDER BY column ASC/DESC
/// - JOIN (INNER, LEFT, RIGHT)
///
/// Not Supported (for now):
/// - Subqueries
/// - UNION/INTERSECT/EXCEPT
/// - HAVING clause
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
    /// Parses SQL text into an AST.
    /// </summary>
    /// <exception cref="SqlParseException">If parsing fails.</exception>
    public SqlSelectStatement Parse()
    {
        _position = 0;
        _commentIndex = 0;

        var lexer = new SqlLexer(_sql);
        var result = lexer.Tokenize();
        _tokens = result.Tokens;
        _comments = result.Comments;

        return ParseSelectStatement();
    }

    /// <summary>
    /// Static convenience method to parse SQL.
    /// </summary>
    public static SqlSelectStatement Parse(string sql)
    {
        var parser = new SqlParser(sql);
        return parser.Parse();
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
    /// Parses a complete SELECT statement.
    /// </summary>
    private SqlSelectStatement ParseSelectStatement()
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

        // Ensure we've consumed all tokens
        if (!IsAtEnd())
        {
            throw Error($"Unexpected token: {Peek().Value}");
        }

        var statement = new SqlSelectStatement(
            columns,
            from,
            joins,
            where,
            orderBy,
            top,
            distinct,
            groupBy);
        statement.LeadingComments.AddRange(leadingComments);

        return statement;
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
    /// Parses a single SELECT column (regular column or aggregate function).
    /// </summary>
    private ISqlSelectColumn ParseSelectColumn()
    {
        if (IsAggregateFunction())
        {
            return ParseAggregateColumn();
        }

        return ParseColumnRef();
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
    /// Parses primary conditions (comparison, LIKE, IS NULL, IN, or parenthesized).
    /// </summary>
    private ISqlCondition ParsePrimaryCondition()
    {
        // Parenthesized condition
        if (Match(SqlTokenType.LeftParen))
        {
            var condition = ParseCondition();
            Expect(SqlTokenType.RightParen);
            AttachTrailingComment(condition);
            return condition;
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
            throw Error("Expected LIKE or IN after NOT");
        }

        // [NOT] IN
        if (Match(SqlTokenType.In))
        {
            var cond = ParseInList(column, false);
            AttachTrailingComment(cond);
            return cond;
        }

        // Comparison operator
        var op = ParseComparisonOperator();
        var value = ParseLiteral();
        var compCond = new SqlComparisonCondition(column, op, value);
        AttachTrailingComment(compCond);
        return compCond;
    }

    /// <summary>
    /// Parses IN (value1, value2, ...) list.
    /// </summary>
    private SqlInCondition ParseInList(SqlColumnRef column, bool isNegated)
    {
        Expect(SqlTokenType.LeftParen);
        var values = new List<SqlLiteral>();

        do
        {
            values.Add(ParseLiteral());
        } while (Match(SqlTokenType.Comma));

        Expect(SqlTokenType.RightParen);
        return new SqlInCondition(column, values, isNegated);
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
    /// Parses a literal value.
    /// </summary>
    private SqlLiteral ParseLiteral()
    {
        if (Match(SqlTokenType.String))
        {
            return SqlLiteral.String(Previous().Value);
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
