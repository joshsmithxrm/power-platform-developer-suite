using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Query.Intellisense;

/// <summary>
/// Produces completion items for FetchXML IntelliSense by using simple XML
/// string parsing to determine cursor context combined with Dataverse metadata
/// from <see cref="ICachedMetadataProvider"/>.
/// </summary>
/// <remarks>
/// Uses heuristic string parsing rather than a full XML parser because the input
/// is often incomplete or invalid XML while being actively edited.
/// Reuses <see cref="SqlCompletion"/> and <see cref="SqlCompletionKind"/> for
/// return-type consistency with <see cref="SqlCompletionEngine"/>.
/// </remarks>
public sealed class FetchXmlCompletionEngine
{
    private readonly ICachedMetadataProvider _metadataProvider;

    // FetchXML operators that can appear on condition[@operator]
    private static readonly string[] FetchXmlOperators =
    {
        "eq", "ne", "gt", "ge", "lt", "le",
        "like", "not-like",
        "in", "not-in",
        "null", "not-null",
        "between", "not-between",
        "above", "under",
        "contain-values",
        "begins-with", "not-begin-with",
        "ends-with", "not-end-with",
        "today", "yesterday", "tomorrow",
        "this-week", "last-week", "next-week",
        "this-month", "last-month", "next-month",
        "this-year", "last-year", "next-year",
        "on", "on-or-before", "on-or-after",
        "last-x-days", "next-x-days",
        "last-x-weeks", "next-x-weeks",
        "last-x-months", "next-x-months",
        "last-x-years", "next-x-years",
        "last-x-hours", "next-x-hours",
    };

    // Attributes that can appear on <fetch ...>
    private static readonly string[] FetchAttributes =
    {
        "version", "count", "page", "paging-cookie",
        "distinct", "aggregate", "top", "returntotalrecordcount",
        "no-lock",
    };

    // Child elements valid inside <entity> or <link-entity>
    private static readonly string[] EntityChildren =
    {
        "attribute", "all-attributes", "filter", "order", "link-entity",
    };

    // Child elements valid inside <filter>
    private static readonly string[] FilterChildren =
    {
        "condition", "filter",
    };

    // Child elements valid directly inside <fetch>
    private static readonly string[] FetchChildren =
    {
        "entity",
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchXmlCompletionEngine"/> class.
    /// </summary>
    /// <param name="metadataProvider">Cached metadata provider for entity/attribute lookups.</param>
    public FetchXmlCompletionEngine(ICachedMetadataProvider metadataProvider)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
    }

    /// <summary>
    /// Gets completion items for the given FetchXML text at the specified cursor offset.
    /// </summary>
    /// <param name="fetchXml">The FetchXML text being edited.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion items sorted by relevance.</returns>
    public async Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string fetchXml, int cursorOffset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fetchXml))
            return Array.Empty<SqlCompletion>();

        cursorOffset = Math.Min(cursorOffset, fetchXml.Length);

        var context = AnalyzeCursorContext(fetchXml, cursorOffset);

        var completions = context.Kind switch
        {
            FetchXmlContextKind.ElementName => GetElementCompletions(context),
            FetchXmlContextKind.XmlAttributeName => GetXmlAttributeNameCompletions(context),
            FetchXmlContextKind.XmlAttributeValue => await GetXmlAttributeValueCompletionsAsync(context, cancellationToken),
            _ => Array.Empty<SqlCompletion>(),
        };

        // Filter by prefix if a partial value is being typed
        if (!string.IsNullOrEmpty(context.Prefix))
        {
            completions = completions
                .Where(c => c.Label.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // Sort: by SortOrder, then alphabetically
        return completions
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    #region Context Analysis

    private FetchXmlCursorContext AnalyzeCursorContext(string xml, int offset)
    {
        // Walk backward from the cursor to find what we're inside.
        var textBefore = xml[..offset];

        // --- Is cursor inside an attribute value? ---
        // Pattern: we are inside a quoted attribute value when there is an open quote
        // after the last attribute-name= that hasn't been closed before the cursor.
        var attrValueCtx = TryGetAttributeValueContext(textBefore);
        if (attrValueCtx != null)
            return attrValueCtx;

        // --- Is cursor after a '<' and we should suggest element names? ---
        // Find the last '<' that does not already have a complete tag name followed by whitespace or >
        if (IsInsideElementNamePosition(textBefore, out var prefix))
        {
            // Determine which parent element the '<' is inside
            var parentElement = FindContainingElement(textBefore);
            return new FetchXmlCursorContext
            {
                Kind = FetchXmlContextKind.ElementName,
                ParentElement = parentElement,
                Prefix = prefix,
            };
        }

        // --- Is cursor after whitespace inside an opening tag? ---
        // Suggest XML attribute names (e.g., after '<fetch ' → version, count, ...)
        if (IsInsideTagAttributeNamePosition(textBefore, out var tagName, out var attrPrefix))
        {
            return new FetchXmlCursorContext
            {
                Kind = FetchXmlContextKind.XmlAttributeName,
                ElementName = tagName,
                Prefix = attrPrefix,
            };
        }

        return new FetchXmlCursorContext { Kind = FetchXmlContextKind.None };
    }

    /// <summary>
    /// Checks whether the cursor is inside an XML attribute value (between quotes).
    /// Returns a context with the attribute name and the containing element name.
    /// </summary>
    private static FetchXmlCursorContext? TryGetAttributeValueContext(string textBefore)
    {
        // Walk backward to find an unmatched open quote preceded by attr="
        // Strategy: find the last '="' or "='" sequence, then check if the quote is still open.
        var pos = textBefore.Length - 1;

        // Find the last open quote (either " or ') that follows an '=' sign
        while (pos >= 0)
        {
            var ch = textBefore[pos];
            if (ch == '"' || ch == '\'')
            {
                // Determine if this is an opening quote (preceded by '=') or a closing quote
                // by counting quotes of the same type before it after the last '<'
                var lastAngle = textBefore.LastIndexOf('<', pos);
                if (lastAngle < 0)
                    break;

                var tagText = textBefore[(lastAngle + 1)..pos];

                // Count unmatched quotes of this character type within the tag text
                var quoteCount = 0;
                var quoteChar = ch;
                foreach (var c in tagText)
                {
                    if (c == quoteChar) quoteCount++;
                }

                // If quoteCount is even, this quote is a closing quote for a prior open
                // If odd, this is an open quote and we are inside a value
                if (quoteCount % 2 == 0)
                {
                    // This quote opens a value — extract the attribute name
                    var attrName = ExtractAttributeNameBeforeQuote(tagText);
                    if (attrName != null)
                    {
                        var elementName = ExtractElementName(tagText);
                        var valuePrefix = textBefore[(pos + 1)..]; // text typed so far in the value
                        return new FetchXmlCursorContext
                        {
                            Kind = FetchXmlContextKind.XmlAttributeValue,
                            ElementName = elementName,
                            AttributeName = attrName,
                            EntityContext = FindCurrentEntityName(textBefore[..lastAngle]),
                            Prefix = valuePrefix,
                        };
                    }
                }
                break;
            }
            pos--;
        }

        return null;
    }

    /// <summary>
    /// Returns whether the cursor is positioned just after a '&lt;' and we should
    /// complete element names. Sets <paramref name="prefix"/> to any partial name typed.
    /// </summary>
    private static bool IsInsideElementNamePosition(string textBefore, out string prefix)
    {
        prefix = "";

        // Walk backward through any identifier characters to find the last '<'
        var pos = textBefore.Length - 1;
        var identStart = pos + 1;

        while (pos >= 0 && IsXmlNameChar(textBefore[pos]))
        {
            pos--;
        }

        if (pos < 0)
            return false;

        if (textBefore[pos] == '<')
        {
            // Make sure this isn't a closing tag (</)
            if (pos > 0 && textBefore[pos - 1] == '/')
                return false;

            // Ensure we're not inside a tag that already has whitespace (attribute position)
            prefix = textBefore[(pos + 1)..];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns whether the cursor is inside an open tag after whitespace,
    /// suggesting XML attribute names. Sets <paramref name="tagName"/> and
    /// <paramref name="attrPrefix"/> accordingly.
    /// </summary>
    private static bool IsInsideTagAttributeNamePosition(
        string textBefore, out string tagName, out string attrPrefix)
    {
        tagName = "";
        attrPrefix = "";

        // Find the last '<' that hasn't been closed by '>'
        var lastAngle = textBefore.LastIndexOf('<');
        if (lastAngle < 0)
            return false;

        var afterAngle = textBefore[(lastAngle + 1)..];

        // If there's a '>' after the last '<', we're not in an open tag
        if (afterAngle.Contains('>'))
            return false;

        // Don't suggest in closing tags
        if (afterAngle.StartsWith('/'))
            return false;

        // Check there's at least one space after the element name (otherwise we're
        // still typing the element name, not an attribute name)
        var spaceIdx = afterAngle.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (spaceIdx < 0)
            return false;

        // Extract element name
        tagName = afterAngle[..spaceIdx];
        if (string.IsNullOrEmpty(tagName))
            return false;

        // Extract what has been typed as attribute name prefix (after the last whitespace)
        var afterSpace = afterAngle[(spaceIdx + 1)..];

        // Walk backward from cursor to find if we're after '=' (in a value) or after whitespace (attr name)
        // If there's an unmatched open quote, it's a value context (handled above)
        // Check for balanced quotes in the remaining text
        var quoteCount = afterSpace.Count(c => c == '"');
        if (quoteCount % 2 != 0)
            return false; // Inside a quoted value

        // Get the partial attribute name being typed (after the last '>' or whitespace following an attribute value)
        var partialAttr = GetPartialAttributeName(afterSpace);
        attrPrefix = partialAttr;
        return true;
    }

    private static string GetPartialAttributeName(string text)
    {
        // Walk backward through identifier chars
        var pos = text.Length - 1;
        while (pos >= 0 && IsXmlNameChar(text[pos]))
        {
            pos--;
        }
        return pos < text.Length - 1 ? text[(pos + 1)..] : "";
    }

    /// <summary>
    /// Finds the logical name of the entity that is in scope at the given position.
    /// Looks for the nearest containing &lt;entity&gt; or &lt;link-entity&gt; element.
    /// </summary>
    private static string? FindCurrentEntityName(string textBefore)
    {
        // Search backward for the most recent <entity name="..." or <link-entity name="..."
        // that is not yet closed.
        var entityName = FindLastOpenEntityName(textBefore, "link-entity")
                      ?? FindLastOpenEntityName(textBefore, "entity");
        return entityName;
    }

    private static string? FindLastOpenEntityName(string text, string elementName)
    {
        var searchFrom = text.Length;
        while (true)
        {
            var openTag = $"<{elementName}";
            var openIdx = text.LastIndexOf(openTag, searchFrom - 1, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0)
                return null;

            // Check it's not a closing tag
            var closeTag = $"</{elementName}>";
            var closeIdx = text.IndexOf(closeTag, openIdx, StringComparison.OrdinalIgnoreCase);
            if (closeIdx >= 0 && closeIdx < text.Length)
            {
                // The element was closed before the cursor — look for an earlier one
                searchFrom = openIdx;
                continue;
            }

            // Extract the name attribute
            var tagSection = text[(openIdx + elementName.Length + 1)..];
            return ExtractAttributeValue(tagSection, "name");
        }
    }

    /// <summary>
    /// Finds the containing element name for a position just after a '&lt;'.
    /// </summary>
    private static string? FindContainingElement(string textBefore)
    {
        // Walk backward looking for an unclosed open tag
        var depth = 0;
        var pos = textBefore.Length - 1;

        // Skip past the '<' we're completing
        while (pos >= 0 && textBefore[pos] == '<') pos--;

        while (pos >= 0)
        {
            if (textBefore[pos] == '>')
            {
                // Find the matching '<'
                var tagStart = textBefore.LastIndexOf('<', pos);
                if (tagStart < 0)
                    break;

                var tag = textBefore[(tagStart + 1)..pos];

                if (tag.StartsWith('/'))
                {
                    // Closing tag — increment depth
                    depth++;
                }
                else if (tag.EndsWith('/') || IsSelfClosingElement(tag))
                {
                    // Self-closing — no depth change
                }
                else
                {
                    // Opening tag
                    if (depth > 0)
                    {
                        depth--;
                    }
                    else
                    {
                        // This is our containing element
                        var spaceIdx = tag.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                        return spaceIdx < 0 ? tag.Trim() : tag[..spaceIdx].Trim();
                    }
                }

                pos = tagStart - 1;
            }
            else
            {
                pos--;
            }
        }

        return null;
    }

    private static bool IsSelfClosingElement(string tagContent)
    {
        // Some FetchXML elements are always self-closing: attribute, all-attributes, order, condition
        var name = tagContent.Split(' ', '\t')[0].TrimEnd('/');
        return name is "attribute" or "all-attributes" or "order" or "condition";
    }

    private static string? ExtractElementName(string tagText)
    {
        var trimmed = tagText.TrimStart();
        var spaceIdx = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        return spaceIdx < 0 ? trimmed.Trim() : trimmed[..spaceIdx].Trim();
    }

    private static string? ExtractAttributeNameBeforeQuote(string tagText)
    {
        // tagText is everything inside the tag up to the opening quote
        // We need to find the attribute name: e.g., 'entity name=' → 'name'
        var pos = tagText.Length - 1;

        // Skip the '=' sign
        while (pos >= 0 && (tagText[pos] == '=' || tagText[pos] == ' ' || tagText[pos] == '\t'))
            pos--;

        if (pos < 0)
            return null;

        var nameEnd = pos + 1;
        while (pos >= 0 && IsXmlNameChar(tagText[pos]))
            pos--;

        return pos < nameEnd - 1 ? tagText[(pos + 1)..nameEnd] : null;
    }

    private static string? ExtractAttributeValue(string tagContent, string attributeName)
    {
        var search = attributeName + "=\"";
        var idx = tagContent.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            search = attributeName + "='";
            idx = tagContent.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
        }

        var valueStart = idx + search.Length;
        var quoteChar = tagContent[idx + attributeName.Length + 1];
        var valueEnd = tagContent.IndexOf(quoteChar, valueStart);
        if (valueEnd < 0)
            return tagContent[valueStart..]; // Unclosed quote

        return tagContent[valueStart..valueEnd];
    }

    private static bool IsXmlNameChar(char ch)
    {
        return ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '-' or '_' or '.' or ':';
    }

    #endregion

    #region Completion Generators

    private static IReadOnlyList<SqlCompletion> GetElementCompletions(FetchXmlCursorContext context)
    {
        var parent = context.ParentElement?.ToLowerInvariant();

        string[] children = parent switch
        {
            "fetch" => FetchChildren,
            "entity" => EntityChildren,
            "link-entity" => EntityChildren, // link-entity can also nest link-entity
            "filter" => FilterChildren,
            _ => Array.Empty<string>(),
        };

        return children.Select((name, i) => new SqlCompletion(
            Label: name,
            InsertText: name,
            Kind: SqlCompletionKind.Keyword,
            SortOrder: i)).ToArray();
    }

    private static IReadOnlyList<SqlCompletion> GetXmlAttributeNameCompletions(FetchXmlCursorContext context)
    {
        var element = context.ElementName?.ToLowerInvariant();

        string[] attrs = element switch
        {
            "fetch" => FetchAttributes,
            "entity" => new[] { "name" },
            "link-entity" => new[] { "name", "from", "to", "alias", "link-type", "intersect" },
            "filter" => new[] { "type" },
            "order" => new[] { "attribute", "alias", "descending" },
            "condition" => new[] { "attribute", "operator", "value", "entityname" },
            "attribute" => new[] { "name", "alias", "aggregate", "groupby", "distinct", "dategrouping" },
            _ => Array.Empty<string>(),
        };

        return attrs.Select((name, i) => new SqlCompletion(
            Label: name,
            InsertText: name,
            Kind: SqlCompletionKind.Keyword,
            SortOrder: i)).ToArray();
    }

    private async Task<IReadOnlyList<SqlCompletion>> GetXmlAttributeValueCompletionsAsync(
        FetchXmlCursorContext context, CancellationToken ct)
    {
        var element = context.ElementName?.ToLowerInvariant();
        var attr = context.AttributeName?.ToLowerInvariant();

        // Entity name attributes
        if (attr == "name" && (element is "entity" or "link-entity"))
        {
            return await GetEntityNameCompletionsAsync(ct);
        }

        // Attribute name in attribute/order/condition
        if (attr == "name" && element == "attribute")
        {
            return await GetAttributeNameCompletionsAsync(context.EntityContext, ct);
        }

        if (attr == "attribute" && element is "order" or "condition")
        {
            return await GetAttributeNameCompletionsAsync(context.EntityContext, ct);
        }

        // from/to on link-entity — suggest attributes (these are typically join keys)
        if (attr is "from" or "to" && element == "link-entity")
        {
            return await GetAttributeNameCompletionsAsync(context.EntityContext, ct);
        }

        // Operator on condition
        if (attr == "operator" && element == "condition")
        {
            return FetchXmlOperators.Select((op, i) => new SqlCompletion(
                Label: op,
                InsertText: op,
                Kind: SqlCompletionKind.Keyword,
                SortOrder: i)).ToArray();
        }

        // type on filter
        if (attr == "type" && element == "filter")
        {
            return new[]
            {
                new SqlCompletion("and", "and", SqlCompletionKind.Keyword, SortOrder: 0),
                new SqlCompletion("or", "or", SqlCompletionKind.Keyword, SortOrder: 1),
            };
        }

        // link-type on link-entity
        if (attr == "link-type" && element == "link-entity")
        {
            return new[]
            {
                new SqlCompletion("inner", "inner", SqlCompletionKind.Keyword, SortOrder: 0),
                new SqlCompletion("outer", "outer", SqlCompletionKind.Keyword, SortOrder: 1),
            };
        }

        // Boolean attributes
        if (attr is "distinct" or "aggregate" or "returntotalrecordcount" or "no-lock"
            or "descending" or "intersect" or "groupby")
        {
            return new[]
            {
                new SqlCompletion("true", "true", SqlCompletionKind.Keyword, SortOrder: 0),
                new SqlCompletion("false", "false", SqlCompletionKind.Keyword, SortOrder: 1),
            };
        }

        return Array.Empty<SqlCompletion>();
    }

    private async Task<IReadOnlyList<SqlCompletion>> GetEntityNameCompletionsAsync(CancellationToken ct)
    {
        var entities = await _metadataProvider.GetEntitiesAsync(ct);
        return entities.Select(e => new SqlCompletion(
            Label: e.LogicalName,
            InsertText: e.LogicalName,
            Kind: SqlCompletionKind.Entity,
            Description: !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : null,
            Detail: e.IsCustomEntity ? "Custom" : "System",
            SortOrder: e.IsCustomEntity ? 0 : 1)).ToArray();
    }

    private async Task<IReadOnlyList<SqlCompletion>> GetAttributeNameCompletionsAsync(
        string? entityName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entityName))
            return Array.Empty<SqlCompletion>();

        try
        {
            var attrs = await _metadataProvider.GetAttributesAsync(entityName, ct);
            return attrs.Select(a =>
            {
                var sortOrder = a.IsPrimaryId ? 0
                    : a.IsPrimaryName ? 1
                    : a.IsCustomAttribute ? 2
                    : 3;

                return new SqlCompletion(
                    Label: a.LogicalName,
                    InsertText: a.LogicalName,
                    Kind: SqlCompletionKind.Attribute,
                    Description: !string.IsNullOrEmpty(a.DisplayName) ? a.DisplayName : null,
                    Detail: a.AttributeType,
                    SortOrder: sortOrder);
            }).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<SqlCompletion>();
        }
    }

    #endregion
}

/// <summary>
/// The kind of completion context identified in FetchXML.
/// </summary>
internal enum FetchXmlContextKind
{
    /// <summary>No completions available at this position.</summary>
    None,

    /// <summary>Cursor is after a '&lt;' — suggest child element names.</summary>
    ElementName,

    /// <summary>Cursor is inside an open tag after whitespace — suggest XML attribute names.</summary>
    XmlAttributeName,

    /// <summary>Cursor is inside a quoted XML attribute value — suggest values.</summary>
    XmlAttributeValue,
}

/// <summary>
/// Describes the cursor context within FetchXML for completion purposes.
/// </summary>
internal sealed class FetchXmlCursorContext
{
    /// <summary>The kind of completion context.</summary>
    public FetchXmlContextKind Kind { get; init; }

    /// <summary>
    /// For <see cref="FetchXmlContextKind.ElementName"/>: the parent element we're inside.
    /// </summary>
    public string? ParentElement { get; init; }

    /// <summary>
    /// For <see cref="FetchXmlContextKind.XmlAttributeName"/> and
    /// <see cref="FetchXmlContextKind.XmlAttributeValue"/>: the element being edited.
    /// </summary>
    public string? ElementName { get; init; }

    /// <summary>
    /// For <see cref="FetchXmlContextKind.XmlAttributeValue"/>: the attribute whose value we're completing.
    /// </summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// For <see cref="FetchXmlContextKind.XmlAttributeValue"/>: the entity name in scope (from the nearest
    /// containing &lt;entity&gt; or &lt;link-entity&gt; element).
    /// </summary>
    public string? EntityContext { get; init; }

    /// <summary>Partial text typed so far that can be used to filter completions.</summary>
    public string Prefix { get; init; } = "";
}
