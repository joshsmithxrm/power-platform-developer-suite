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
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("tax");

        var binary = computed.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var left = binary.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("revenue");

        var right = binary.Right as SqlLiteralExpression;
        right.Should().NotBeNull();
        right!.Value.Value.Should().Be("0.1");
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
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("total");

        var binary = computed.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Add);

        var left = binary.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("price");

        var right = binary.Right as SqlColumnExpression;
        right.Should().NotBeNull();
        right!.Column.ColumnName.Should().Be("shipping");
    }

    [Fact]
    public void Parse_ArithmeticExpression_OperatorPrecedence()
    {
        // Arrange — b * c should be grouped first (higher precedence than +)
        var sql = "SELECT a + b * c FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();

        // Top-level should be Add: a + (b * c)
        var add = computed!.Expression as SqlBinaryExpression;
        add.Should().NotBeNull();
        add!.Operator.Should().Be(SqlBinaryOperator.Add);

        var left = add.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("a");

        // Right side should be Multiply: b * c
        var multiply = add.Right as SqlBinaryExpression;
        multiply.Should().NotBeNull();
        multiply!.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var mulLeft = multiply.Left as SqlColumnExpression;
        mulLeft.Should().NotBeNull();
        mulLeft!.Column.ColumnName.Should().Be("b");

        var mulRight = multiply.Right as SqlColumnExpression;
        mulRight.Should().NotBeNull();
        mulRight!.Column.ColumnName.Should().Be("c");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Parentheses()
    {
        // Arrange — parentheses override precedence: (a + b) * c
        var sql = "SELECT (a + b) * c FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();

        // Top-level should be Multiply: (a + b) * c
        var multiply = computed!.Expression as SqlBinaryExpression;
        multiply.Should().NotBeNull();
        multiply!.Operator.Should().Be(SqlBinaryOperator.Multiply);

        // Left side should be Add: a + b
        var add = multiply.Left as SqlBinaryExpression;
        add.Should().NotBeNull();
        add!.Operator.Should().Be(SqlBinaryOperator.Add);

        var addLeft = add.Left as SqlColumnExpression;
        addLeft.Should().NotBeNull();
        addLeft!.Column.ColumnName.Should().Be("a");

        var addRight = add.Right as SqlColumnExpression;
        addRight.Should().NotBeNull();
        addRight!.Column.ColumnName.Should().Be("b");

        var mulRight = multiply.Right as SqlColumnExpression;
        mulRight.Should().NotBeNull();
        mulRight!.Column.ColumnName.Should().Be("c");
    }

    [Fact]
    public void Parse_ArithmeticExpression_StringConcat()
    {
        // Arrange — string concatenation with + operator
        var sql = "SELECT firstname + ' ' + lastname AS fullname FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("fullname");

        // Top-level: (firstname + ' ') + lastname (left-associative)
        var outerAdd = computed.Expression as SqlBinaryExpression;
        outerAdd.Should().NotBeNull();
        outerAdd!.Operator.Should().Be(SqlBinaryOperator.Add);

        var innerAdd = outerAdd.Left as SqlBinaryExpression;
        innerAdd.Should().NotBeNull();
        innerAdd!.Operator.Should().Be(SqlBinaryOperator.Add);

        var firstname = innerAdd.Left as SqlColumnExpression;
        firstname.Should().NotBeNull();
        firstname!.Column.ColumnName.Should().Be("firstname");

        var space = innerAdd.Right as SqlLiteralExpression;
        space.Should().NotBeNull();
        space!.Value.Value.Should().Be(" ");

        var lastname = outerAdd.Right as SqlColumnExpression;
        lastname.Should().NotBeNull();
        lastname!.Column.ColumnName.Should().Be("lastname");
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

        var col1 = result.Columns[0] as SqlColumnRef;
        col1.Should().NotBeNull();
        col1!.ColumnName.Should().Be("name");

        var col2 = result.Columns[1] as SqlComputedColumn;
        col2.Should().NotBeNull();
        col2!.Alias.Should().Be("tax");

        var binary = col2.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Multiply);
    }

    [Fact]
    public void Parse_ArithmeticExpression_Subtraction()
    {
        // Arrange
        var sql = "SELECT revenue - cost AS profit FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("profit");

        var binary = computed.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Subtract);

        var left = binary.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("revenue");

        var right = binary.Right as SqlColumnExpression;
        right.Should().NotBeNull();
        right!.Column.ColumnName.Should().Be("cost");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Division()
    {
        // Arrange
        var sql = "SELECT total / quantity AS average FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("average");

        var binary = computed.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Divide);

        var left = binary.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("total");

        var right = binary.Right as SqlColumnExpression;
        right.Should().NotBeNull();
        right!.Column.ColumnName.Should().Be("quantity");
    }

    [Fact]
    public void Parse_ArithmeticExpression_Modulo()
    {
        // Arrange
        var sql = "SELECT value % 2 AS remainder FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("remainder");

        var binary = computed.Expression as SqlBinaryExpression;
        binary.Should().NotBeNull();
        binary!.Operator.Should().Be(SqlBinaryOperator.Modulo);

        var left = binary.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.ColumnName.Should().Be("value");

        var right = binary.Right as SqlLiteralExpression;
        right.Should().NotBeNull();
        right!.Value.Value.Should().Be("2");
    }

    [Fact]
    public void Parse_ArithmeticExpression_UnaryNegation()
    {
        // Arrange
        var sql = "SELECT -amount AS negated FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var computed = result.Columns[0] as SqlComputedColumn;
        computed.Should().NotBeNull();
        computed!.Alias.Should().Be("negated");

        var unary = computed.Expression as SqlUnaryExpression;
        unary.Should().NotBeNull();
        unary!.Operator.Should().Be(SqlUnaryOperator.Negate);

        var operand = unary.Operand as SqlColumnExpression;
        operand.Should().NotBeNull();
        operand!.Column.ColumnName.Should().Be("amount");
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
        var col = result.Columns[0] as SqlColumnRef;
        col.Should().NotBeNull();
        col!.IsWildcard.Should().BeTrue();
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
        var col = result.Columns[0] as SqlColumnRef;
        col.Should().NotBeNull();
        col!.IsWildcard.Should().BeTrue();
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
        var comp = result.Where as SqlComparisonCondition;
        comp.Should().NotBeNull();
        comp!.Value.Value.Should().Be("-5");
    }
}
