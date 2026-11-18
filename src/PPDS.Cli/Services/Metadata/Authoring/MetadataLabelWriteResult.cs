using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Outcome of an update that may write labels. Every label PPDS writes is the organisation's base-language label,
/// merged with the other languages. When the label Dataverse shows the caller (the user-localised label) is a stored
/// translation in another language that does not already read the requested text, that translation is left unchanged
/// and <see cref="ValidationMessages"/> says so (rule <see cref="BaseLanguageLabelOnlyRule"/>). Every surface (CLI, TUI,
/// MCP, RPC) shows these notices.
/// </summary>
public sealed class MetadataLabelWriteResult
{
    /// <summary>Rule identifier of the notice that only the base-language label was changed.</summary>
    public const string BaseLanguageLabelOnlyRule = "BASE_LANGUAGE_LABEL_ONLY";

    /// <summary>An outcome with no notices.</summary>
    public static MetadataLabelWriteResult None { get; } = new();

    /// <summary>Notices about the labels written; empty when there is nothing to report.</summary>
    public IReadOnlyList<ValidationMessage> ValidationMessages { get; init; } = [];
}
