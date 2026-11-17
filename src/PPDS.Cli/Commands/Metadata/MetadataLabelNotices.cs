using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Surfaces <see cref="MetadataLabelWriteResult"/> notices: a stderr warning in text mode and an additive
/// <c>validationMessages</c> array in JSON (omitted when there is nothing to report).
/// </summary>
internal static class MetadataLabelNotices
{
    internal static IReadOnlyList<ValidationMessage>? ForJson(MetadataLabelWriteResult? result)
        => result?.ValidationMessages is { Count: > 0 } messages ? messages : null;

    internal static void WriteWarnings(MetadataLabelWriteResult? result)
    {
        foreach (var message in result?.ValidationMessages ?? [])
            Console.Error.WriteLine($"Warning: {message.Message}");
    }
}
