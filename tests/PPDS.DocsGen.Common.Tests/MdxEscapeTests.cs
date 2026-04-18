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
    public void InlineCode_ContainsSingleBacktick_WrapsInDoubleBacktick()
    {
        // Input has backticks but no ``` run — double-backtick wrapper is the
        // canonical choice so the outer delimiter differs from inner content.
        var result = MdxEscape.InlineCode("foo `bar`");

        result.Should().Be("``foo `bar```");
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
