using FluentAssertions;
using PPDS.DocsGen.Common;
using Xunit;

namespace PPDS.DocsGen.Common.Tests;

public class MdxEscapeTests
{
    [Fact]
    public void Prose_EscapesAngleBracketsInProse()
    {
        var result = MdxEscape.Prose("uses Task<T> heavily");

        result.Should().Be("uses Task&lt;T&gt; heavily");
    }

    [Fact]
    public void Prose_PreservesFencedCode()
    {
        // Fenced block contents must pass through verbatim — MDX parses fence
        // bodies as raw text, so the raw `<` is safe (and escaping it would
        // corrupt the code sample).
        var input = "text\n```csharp\nTask<T>\n```\nmore";

        var result = MdxEscape.Prose(input);

        // The inner Task<T> must remain unescaped; prose around it is unchanged
        // because there are no angle brackets outside the fence.
        result.Should().Be(input);
    }

    [Fact]
    public void Prose_PreservesInlineCode()
    {
        var result = MdxEscape.Prose("use `Task<T>` here");

        result.Should().Be("use `Task<T>` here");
    }

    [Fact]
    public void Prose_EscapesAmpersandBeforeAngleBrackets()
    {
        // Ordering check: `&` must be replaced first, otherwise the `&` in the
        // `&lt;` / `&gt;` entities produced later would themselves be escaped.
        var result = MdxEscape.Prose("a & b < c");

        result.Should().Be("a &amp; b &lt; c");
    }

    [Fact]
    public void InlineCode_NoBacktick_WrapsInSingleBacktick()
    {
        var result = MdxEscape.InlineCode("Task<T>");

        result.Should().Be("`Task<T>`");
    }

    [Fact]
    public void InlineCode_ContainsSingleBacktick_WrapsInDoubleBacktickWithPadding()
    {
        // Input has a max backtick run of 1 → delimiter is 2 backticks. Content
        // ends with a backtick, so a space is padded before the closing
        // delimiter to prevent the last backtick of content from merging with
        // the delimiter run.
        var result = MdxEscape.InlineCode("foo `bar`");

        result.Should().Be("``foo `bar` ``");
    }

    [Fact]
    public void InlineCode_ContainsDoubleBacktickRun_WrapsInTripleBacktick()
    {
        // Content contains a run of 2 backticks → delimiter must be 3.
        var result = MdxEscape.InlineCode("a``b");

        result.Should().Be("```a``b```");
    }

    [Fact]
    public void InlineCode_ContainsTripleBacktickRun_UsesFourBacktickDelimiter()
    {
        // Content contains a run of 3 backticks → delimiter must be 4. No
        // fallback to a fenced block — inline code stays inline.
        var result = MdxEscape.InlineCode("x```y");

        result.Should().Be("````x```y````");
    }

    [Fact]
    public void InlineCode_StartsAndEndsWithBacktick_PadsBothSides()
    {
        var result = MdxEscape.InlineCode("`hello`");

        result.Should().Be("`` `hello` ``");
    }

    [Fact]
    public void Heading_EscapesAngleBrackets()
    {
        var result = MdxEscape.Heading("GenericProcessor<TRequest, TResponse>");

        result.Should().Be("GenericProcessor&lt;TRequest, TResponse&gt;");
    }

    [Fact]
    public void Heading_EscapesAmpersandBeforeAngleBrackets()
    {
        var result = MdxEscape.Heading("A & B<C>");

        result.Should().Be("A &amp; B&lt;C&gt;");
    }

    [Fact]
    public void Heading_PassesThroughPlainText()
    {
        var result = MdxEscape.Heading("Widget");

        result.Should().Be("Widget");
    }

    [Fact]
    public void Heading_ReturnsEmptyForEmpty()
    {
        MdxEscape.Heading(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Heading_ReturnsNullForNull()
    {
        MdxEscape.Heading(null!).Should().BeNull();
    }

    [Fact]
    public void InlineCode_Deterministic()
    {
        // AC-19 determinism: same input → byte-identical output on repeat calls.
        const string input = "Dictionary<string, List<int>>";

        var first = MdxEscape.InlineCode(input);
        var second = MdxEscape.InlineCode(input);

        first.Should().Be(second);
    }
}
