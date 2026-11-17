using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Outcome of an update that may write labels. Every label PPDS writes is the English (LCID 1033) label, merged with
/// the other languages. When the target's label in the caller's own language is not English, that label is left
/// unchanged, and <see cref="ValidationMessages"/> says so (rule <see cref="EnglishLabelOnlyRule"/>).
/// </summary>
public sealed class MetadataLabelWriteResult
{
    /// <summary>Rule identifier of the notice that only the English label was changed.</summary>
    public const string EnglishLabelOnlyRule = "ENGLISH_LABEL_ONLY";

    /// <summary>An outcome with no notices.</summary>
    public static MetadataLabelWriteResult None { get; } = new();

    /// <summary>Notices about the labels written; empty when there is nothing to report.</summary>
    public IReadOnlyList<ValidationMessage> ValidationMessages { get; init; } = [];
}
