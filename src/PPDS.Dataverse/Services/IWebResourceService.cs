using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Models;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying, updating, and publishing Dataverse web resources.
/// </summary>
public interface IWebResourceService
{
    /// <summary>
    /// Lists web resources, optionally filtered by solution and/or type.
    /// </summary>
    /// <param name="solutionId">Optional solution ID filter. Uses ConditionOperator.In with component IDs.</param>
    /// <param name="textOnly">If true, excludes binary types (PNG/JPG/GIF/XAP/ICO).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ListResult<WebResourceInfo>> ListAsync(
        Guid? solutionId = null,
        bool textOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a web resource by ID (metadata only, no content).
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebResourceInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the content of a web resource, optionally unpublished.
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="published">If true, gets published content (standard query). If false, uses RetrieveUnpublished.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Base64-decoded string content for text types, null for binary or missing content.</returns>
    Task<WebResourceContent?> GetContentAsync(
        Guid id,
        bool published = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets only the modifiedOn timestamp for conflict detection (lightweight query).
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DateTime?> GetModifiedOnAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the content of a web resource. Does NOT publish.
    /// </summary>
    /// <param name="id">The web resource ID.</param>
    /// <param name="content">The text content (will be base64 encoded before sending).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateContentAsync(Guid id, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes specific web resources via PublishXml.
    /// </summary>
    /// <param name="ids">The web resource IDs to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of web resources published.</returns>
    Task<int> PublishAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes all customizations via PublishAllXml.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Web resource metadata (no content).
/// </summary>
public record WebResourceInfo(
    Guid Id,
    string Name,
    string? DisplayName,
    int WebResourceType,
    bool IsManaged,
    string? CreatedByName,
    DateTime? CreatedOn,
    string? ModifiedByName,
    DateTime? ModifiedOn)
{
    /// <summary>
    /// Human-readable type name derived from type code.
    /// Single code path for all surfaces (Constitution A2).
    /// </summary>
    public string TypeName => WebResourceType switch
    {
        1 => "HTML",
        2 => "CSS",
        3 => "JavaScript",
        4 => "XML",
        5 => "PNG",
        6 => "JPG",
        7 => "GIF",
        8 => "XAP (Silverlight)",
        9 => "XSL",
        10 => "ICO",
        11 => "SVG",
        12 => "RESX",
        _ => $"Unknown ({WebResourceType})"
    };

    /// <summary>
    /// File extension derived from type code (without dot).
    /// </summary>
    public string FileExtension => WebResourceType switch
    {
        1 => "html",
        2 => "css",
        3 => "js",
        4 => "xml",
        5 => "png",
        6 => "jpg",
        7 => "gif",
        8 => "xap",
        9 => "xsl",
        10 => "ico",
        11 => "svg",
        12 => "resx",
        _ => "bin"
    };

    /// <summary>
    /// Whether this type is text-based and editable in an editor.
    /// </summary>
    public bool IsTextType => WebResourceType is 1 or 2 or 3 or 4 or 9 or 11 or 12;
}

/// <summary>
/// Web resource content with metadata needed for editing.
/// </summary>
public record WebResourceContent(
    Guid Id,
    string Name,
    int WebResourceType,
    string? Content,
    DateTime? ModifiedOn);
