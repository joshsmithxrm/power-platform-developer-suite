using FluentAssertions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Sql.Intellisense;
using Xunit;

namespace PPDS.Cli.Tests.Services;

[Trait("Category", "Unit")]
public class SqlLanguageServiceTests
{
    #region Tokenize

    [Fact]
    public void Tokenize_ReturnsTokens_ForValidSql()
    {
        // Arrange
        var sut = new SqlLanguageService(metadataProvider: null);

        // Act
        var tokens = sut.Tokenize("SELECT name FROM account");

        // Assert
        tokens.Should().NotBeEmpty();
        tokens.Should().Contain(t => t.Type == SourceTokenType.Keyword);
        tokens.Should().Contain(t => t.Type == SourceTokenType.Identifier);
    }

    [Fact]
    public void Tokenize_ReturnsEmpty_ForEmptyInput()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var tokens = sut.Tokenize("");

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_ReturnsEmpty_ForNullInput()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var tokens = sut.Tokenize(null!);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_IdentifiesKeywordsAndIdentifiers()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var tokens = sut.Tokenize("SELECT name FROM account WHERE id = 1");

        // SELECT, FROM, WHERE should be keywords
        var keywordTokens = tokens.Where(t => t.Type == SourceTokenType.Keyword).ToList();
        keywordTokens.Should().HaveCountGreaterThanOrEqualTo(3);

        // name, account, id should be identifiers
        var identifierTokens = tokens.Where(t => t.Type == SourceTokenType.Identifier).ToList();
        identifierTokens.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Tokenize_ClassifiesStringLiterals()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var tokens = sut.Tokenize("SELECT name FROM account WHERE name = 'test'");

        tokens.Should().Contain(t => t.Type == SourceTokenType.StringLiteral);
    }

    [Fact]
    public void Tokenize_ClassifiesNumericLiterals()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var tokens = sut.Tokenize("SELECT TOP 10 name FROM account");

        tokens.Should().Contain(t => t.Type == SourceTokenType.NumericLiteral);
    }

    [Fact]
    public void Tokenize_DoesNotThrow_ForMalformedSql()
    {
        // Interface says: "Never throws - invalid input produces Error tokens"
        var sut = new SqlLanguageService(metadataProvider: null);

        var act = () => sut.Tokenize("SELECT ,,, FROM @@@");

        act.Should().NotThrow();
    }

    #endregion

    #region GetCompletionsAsync — null metadata (keyword fallback)

    [Fact]
    public async Task GetCompletionsAsync_ReturnsKeywordFallback_WhenNoMetadataProvider()
    {
        // When metadataProvider is null, _completionEngine is null.
        // The fallback path returns keyword suggestions from SqlCursorContext.Analyze.
        var sut = new SqlLanguageService(metadataProvider: null);

        // Empty SQL at offset 0 triggers StatementStart keywords (SELECT, INSERT, etc.)
        var completions = await sut.GetCompletionsAsync("", 0);

        completions.Should().NotBeEmpty();
        completions.Should().OnlyContain(c => c.Kind == SqlCompletionKind.Keyword);
        completions.Should().Contain(c => c.Label == "SELECT");
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsEmpty_WhenContextIsEntityKind_NoMetadata()
    {
        // After FROM, context.Kind is Entity — needs metadata to produce completions.
        // With no metadata provider, the keyword-only fallback skips non-Keyword kinds.
        var sut = new SqlLanguageService(metadataProvider: null);

        var completions = await sut.GetCompletionsAsync("SELECT name FROM ", 17);

        completions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCompletionsAsync_ThreadsCancellationToken()
    {
        var sut = new SqlLanguageService(metadataProvider: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // With null metadata provider, the fallback path doesn't check ct explicitly.
        // The method should still complete (keyword fallback is sync under the hood).
        // This verifies the ct parameter is accepted without error.
        var completions = await sut.GetCompletionsAsync("", 0, cts.Token);
        completions.Should().NotBeNull();
    }

    #endregion

    #region GetCompletionsAsync — with metadata provider

    [Fact]
    public async Task GetCompletionsAsync_ReturnsCompletions_WithMetadataProvider()
    {
        // Arrange: provide a metadata provider so _completionEngine is created
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new() { LogicalName = "account", DisplayName = "Account", SchemaName = "Account", IsCustomEntity = false },
                new() { LogicalName = "contact", DisplayName = "Contact", SchemaName = "Contact", IsCustomEntity = false }
            });

        var sut = new SqlLanguageService(mockMetadata.Object);

        // "SELECT name FROM " — cursor after FROM triggers entity completions
        var completions = await sut.GetCompletionsAsync("SELECT name FROM ", 17);

        completions.Should().NotBeEmpty();
        completions.Should().Contain(c => c.Label == "account");
        completions.Should().Contain(c => c.Label == "contact");
    }

    [Fact]
    public async Task GetCompletionsAsync_WrapsException_InPpdsException()
    {
        // Arrange: make the metadata provider throw when GetEntitiesAsync is called
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var sut = new SqlLanguageService(mockMetadata.Object);

        // Act: trigger the completion engine path that calls metadata
        // "SELECT name FROM " at offset 17 triggers Entity context in completion engine
        var act = () => sut.GetCompletionsAsync("SELECT name FROM ", 17);

        // Assert: should wrap in PpdsException with CompletionFailed error code
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.CompletionFailed);
        ex.Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task GetCompletionsAsync_RethrowsOperationCanceledException()
    {
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new SqlLanguageService(mockMetadata.Object);

        var act = () => sut.GetCompletionsAsync("SELECT name FROM ", 17);

        // OperationCanceledException must NOT be wrapped in PpdsException
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region ValidateAsync

    [Fact]
    public async Task ValidateAsync_ReturnsNoDiagnostics_ForValidSql()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var diagnostics = await sut.ValidateAsync("SELECT name FROM account");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ReturnsDiagnostics_ForInvalidSql()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var diagnostics = await sut.ValidateAsync("SELEC name FORM account");

        diagnostics.Should().NotBeEmpty();
        diagnostics.Should().Contain(d => d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsEmpty_ForEmptySql()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var diagnostics = await sut.ValidateAsync("");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ReturnsEmpty_ForWhitespaceSql()
    {
        var sut = new SqlLanguageService(metadataProvider: null);

        var diagnostics = await sut.ValidateAsync("   ");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ReportsUnknownEntity_WithMetadataProvider()
    {
        // With a metadata provider that has no entities, unknown entity warnings are produced
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>());

        var sut = new SqlLanguageService(mockMetadata.Object);

        var diagnostics = await sut.ValidateAsync("SELECT name FROM nonexistent_table");

        diagnostics.Should().NotBeEmpty();
        diagnostics.Should().Contain(d => d.Message.Contains("Unknown entity"));
    }

    [Fact]
    public async Task ValidateAsync_ThreadsCancellationToken()
    {
        // Verify the CancellationToken parameter is accepted
        var sut = new SqlLanguageService(metadataProvider: null);
        using var cts = new CancellationTokenSource();

        var diagnostics = await sut.ValidateAsync("SELECT 1", cts.Token);

        diagnostics.Should().BeEmpty();
    }

    #endregion

    #region ValidateAsync — exception wrapping

    [Fact]
    public async Task ValidateAsync_WrapsException_InPpdsException()
    {
        // The PpdsException wrapping in ValidateAsync catches exceptions from
        // _validator.ValidateAsync. Since SqlValidator is created internally with the
        // metadataProvider, we need to trigger a failure in the validator's semantic
        // validation path.
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Metadata fetch failed"));

        var sut = new SqlLanguageService(mockMetadata.Object);

        // Valid SQL so parse succeeds, then semantic validation calls GetEntitiesAsync which throws
        var act = () => sut.ValidateAsync("SELECT name FROM account");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.ValidationFailed);
        ex.Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ValidateAsync_RethrowsOperationCanceledException()
    {
        var mockMetadata = new Mock<ICachedMetadataProvider>();
        mockMetadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new SqlLanguageService(mockMetadata.Object);

        var act = () => sut.ValidateAsync("SELECT name FROM account");

        // OperationCanceledException must NOT be wrapped in PpdsException
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_AcceptsNullMetadataProvider()
    {
        // metadataProvider is nullable per the constructor signature
        var act = () => new SqlLanguageService(metadataProvider: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_AcceptsNonNullMetadataProvider()
    {
        var mockMetadata = new Mock<ICachedMetadataProvider>();

        var act = () => new SqlLanguageService(mockMetadata.Object);

        act.Should().NotThrow();
    }

    #endregion
}
