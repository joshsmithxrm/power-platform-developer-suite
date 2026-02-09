using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class ExpressionParserTests
{
    [Fact]
    public void Parse_ArithmeticExpression_MultiplyWithAlias()
    {
        // Arrange
        var sql = "SELECT revenue * 0.1 AS tax FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Columns.Should().HaveCount(1);
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("tax");

        var binary = (SqlBinaryExpression)computed.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var left = (SqlColumnExpression)binary.Left;
        left.Column.ColumnName.Should().Be("revenue");

        var right = (SqlLiteralExpression)binary.Right;
        right.Value.Value.Should().Be("0.1");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Addition()
    {
        // Arrange
        var sql = "SELECT price + shipping AS total FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Columns.Should().HaveCount(1);
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("total");

        var binary = (SqlBinaryExpression)computed.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Add);

        var left = (SqlColumnExpression)binary.Left;
        left.Column.ColumnName.Should().Be("price");

        var right = (SqlColumnExpression)binary.Right;
        right.Column.ColumnName.Should().Be("shipping");
    }

    [Fact]
    public void Parse_ArithmeticExpression_OperatorPrecedence()
    {
        // Arrange — b * c should be grouped first (higher precedence than +)
        var sql = "SELECT a + b * c FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];

        // Top-level should be Add: a + (b * c)
        var add = (SqlBinaryExpression)computed.Expression;
        add.Operator.Should().Be(SqlBinaryOperator.Add);

        var left = (SqlColumnExpression)add.Left;
        left.Column.ColumnName.Should().Be("a");

        // Right side should be Multiply: b * c
        var multiply = (SqlBinaryExpression)add.Right;
        multiply.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var mulLeft = (SqlColumnExpression)multiply.Left;
        mulLeft.Column.ColumnName.Should().Be("b");

        var mulRight = (SqlColumnExpression)multiply.Right;
        mulRight.Column.ColumnName.Should().Be("c");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Parentheses()
    {
        // Arrange — parentheses override precedence: (a + b) * c
        var sql = "SELECT (a + b) * c FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];

        // Top-level should be Multiply: (a + b) * c
        var multiply = (SqlBinaryExpression)computed.Expression;
        multiply.Operator.Should().Be(SqlBinaryOperator.Multiply);

        // Left side should be Add: a + b
        var add = (SqlBinaryExpression)multiply.Left;
        add.Operator.Should().Be(SqlBinaryOperator.Add);

        var addLeft = (SqlColumnExpression)add.Left;
        addLeft.Column.ColumnName.Should().Be("a");

        var addRight = (SqlColumnExpression)add.Right;
        addRight.Column.ColumnName.Should().Be("b");

        var mulRight = (SqlColumnExpression)multiply.Right;
        mulRight.Column.ColumnName.Should().Be("c");
    }

    [Fact]
    public void Parse_ArithmeticExpression_StringConcat()
    {
        // Arrange — string concatenation with + operator
        var sql = "SELECT firstname + ' ' + lastname AS fullname FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("fullname");

        // Top-level: (firstname + ' ') + lastname (left-associative)
        var outerAdd = (SqlBinaryExpression)computed.Expression;
        outerAdd.Operator.Should().Be(SqlBinaryOperator.Add);

        var innerAdd = (SqlBinaryExpression)outerAdd.Left;
        innerAdd.Operator.Should().Be(SqlBinaryOperator.Add);

        var firstname = (SqlColumnExpression)innerAdd.Left;
        firstname.Column.ColumnName.Should().Be("firstname");

        var space = (SqlLiteralExpression)innerAdd.Right;
        space.Value.Value.Should().Be(" ");

        var lastname = (SqlColumnExpression)outerAdd.Right;
        lastname.Column.ColumnName.Should().Be("lastname");
    }

    [Fact]
    public void Parse_ArithmeticExpression_MixedWithColumns()
    {
        // Arrange — mix of regular column and computed column
        var sql = "SELECT name, revenue * 0.1 AS tax FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Columns.Should().HaveCount(2);

        var col1 = (SqlColumnRef)result.Columns[0];
        col1.ColumnName.Should().Be("name");

        var col2 = (SqlComputedColumn)result.Columns[1];
        col2.Alias.Should().Be("tax");

        var binary = (SqlBinaryExpression)col2.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Multiply);
    }

    [Fact]
    public void Parse_ArithmeticExpression_Subtraction()
    {
        // Arrange
        var sql = "SELECT revenue - cost AS profit FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("profit");

        var binary = (SqlBinaryExpression)computed.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Subtract);

        var left = (SqlColumnExpression)binary.Left;
        left.Column.ColumnName.Should().Be("revenue");

        var right = (SqlColumnExpression)binary.Right;
        right.Column.ColumnName.Should().Be("cost");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Division()
    {
        // Arrange
        var sql = "SELECT total / quantity AS average FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("average");

        var binary = (SqlBinaryExpression)computed.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Divide);

        var left = (SqlColumnExpression)binary.Left;
        left.Column.ColumnName.Should().Be("total");

        var right = (SqlColumnExpression)binary.Right;
        right.Column.ColumnName.Should().Be("quantity");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Modulo()
    {
        // Arrange
        var sql = "SELECT value % 2 AS remainder FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("remainder");

        var binary = (SqlBinaryExpression)computed.Expression;
        binary.Operator.Should().Be(SqlBinaryOperator.Modulo);

        var left = (SqlColumnExpression)binary.Left;
        left.Column.ColumnName.Should().Be("value");

        var right = (SqlLiteralExpression)binary.Right;
        right.Value.Value.Should().Be("2");
    }

    [Fact]
    public void Parse_ArithmeticExpression_UnaryNegation()
    {
        // Arrange
        var sql = "SELECT -amount AS negated FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("negated");

        var unary = (SqlUnaryExpression)computed.Expression;
        unary.Operator.Should().Be(SqlUnaryOperator.Negate);

        var operand = (SqlColumnExpression)unary.Operand;
        operand.Column.ColumnName.Should().Be("amount");
    }

    [Fact]
    public void Parse_SelectStar_StillWorks()
    {
        // Arrange — ensure SELECT * still works after arithmetic changes
        var sql = "SELECT * FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Columns.Should().HaveCount(1);
        var col = (SqlColumnRef)result.Columns[0];
        col.IsWildcard.Should().BeTrue();
    }

    [Fact]
    public void Parse_SelectTableDotStar_StillWorks()
    {
        // Arrange — ensure SELECT t.* still works
        var sql = "SELECT a.* FROM account a";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Columns.Should().HaveCount(1);
        var col = (SqlColumnRef)result.Columns[0];
        col.IsWildcard.Should().BeTrue();
        col.TableName.Should().Be("a");
    }

    [Fact]
    public void Parse_NegativeNumberInWhere_StillWorks()
    {
        // Arrange — ensure WHERE column = -5 still works after lexer changes
        var sql = "SELECT name FROM account WHERE balance = -5";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Where.Should().NotBeNull();
        var comp = (SqlComparisonCondition)result.Where!;
        comp.Value.Value.Should().Be("-5");
    }
}
