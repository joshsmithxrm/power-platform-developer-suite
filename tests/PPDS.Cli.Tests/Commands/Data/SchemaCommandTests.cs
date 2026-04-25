using FluentAssertions;
using Moq;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

public class SchemaCommandTests
{
    #region Command Structure

    [Fact]
    public void Create_HasFilterOption()
    {
        var command = SchemaCommand.Create();

        var option = command.Options.FirstOrDefault(o => o.Name == "--filter");
        option.Should().NotBeNull();
        option!.Required.Should().BeFalse();
    }

    #endregion

    #region ParseAndTranspileFilters

    [Fact]
    public void ParseAndTranspileFilters_ValidFilter_ReturnsDictionary()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:statecode = 0" }, entitySet, writer.Object);

        result.Should().NotBeNull();
        result.Should().ContainKey("account");
        result!["account"].Should().Contain("statecode");
    }

    [Fact]
    public void ParseAndTranspileFilters_MultipleFilters_ReturnsDictionaryWithAll()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account", "contact" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:statecode = 0", "contact:createdon > '2024-01-01'" },
            entitySet, writer.Object);

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().ContainKey("account");
        result.Should().ContainKey("contact");
    }

    [Fact]
    public void ParseAndTranspileFilters_MissingColon_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "statecode = 0" }, entitySet, writer.Object);

        result.Should().BeNull();
        writer.Verify(w => w.WriteError(It.Is<StructuredError>(e =>
            e.Code == ErrorCodes.Validation.InvalidValue)), Times.Once);
    }

    [Fact]
    public void ParseAndTranspileFilters_EntityNotInList_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "contact:statecode = 0" }, entitySet, writer.Object);

        result.Should().BeNull();
        writer.Verify(w => w.WriteError(It.Is<StructuredError>(e =>
            e.Code == ErrorCodes.Validation.InvalidValue)), Times.Once);
    }

    [Fact]
    public void ParseAndTranspileFilters_InvalidExpression_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:NOT VALID %%%" }, entitySet, writer.Object);

        result.Should().BeNull();
        writer.Verify(w => w.WriteError(It.IsAny<StructuredError>()), Times.Once);
    }

    [Fact]
    public void ParseAndTranspileFilters_EmptyExpression_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:" }, entitySet, writer.Object);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseAndTranspileFilters_EmptyEntityName_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { ":statecode = 0" }, entitySet, writer.Object);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseAndTranspileFilters_CaseInsensitiveEntityMatch()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:statecode = 0" }, entitySet, writer.Object);

        result.Should().NotBeNull();
    }

    [Fact]
    public void ParseAndTranspileFilters_DuplicateEntity_ReturnsNull()
    {
        var entitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" };
        var writer = new Mock<IOutputWriter>();

        var result = SchemaCommand.ParseAndTranspileFilters(
            new[] { "account:statecode = 0", "account:name LIKE '%test%'" },
            entitySet, writer.Object);

        result.Should().BeNull();
        writer.Verify(w => w.WriteError(It.Is<StructuredError>(e =>
            e.Code == ErrorCodes.Validation.InvalidValue &&
            e.Message.Contains("Duplicate"))), Times.Once);
    }

    #endregion
}
