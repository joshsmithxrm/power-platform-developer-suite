using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PPDS.Auth.Profiles;

/// <summary>
/// The environments file holds something PPDS cannot read: a value that names no member, such as
/// <c>"type": 1</c>, or text that is not valid JSON. The message names the file by its full path, the entry and the
/// field, says which values are accepted and says to correct the field by hand.
/// </summary>
/// <remarks>
/// Every reader of the file fails with this, so no write runs with a type or protection PPDS cannot establish
/// (#1829). No command repairs the file and none skips the entry. It derives from <see cref="JsonException"/>, so
/// a caller that handled a failed read before still handles it. Each surface reports it with <see cref="ErrorCode"/>.
/// </remarks>
public sealed class EnvironmentConfigFileException : JsonException
{
    /// <summary>The error code every surface reports (<c>ErrorCodes.Validation.EnvironmentConfigInvalid</c> in the CLI).</summary>
    public const string Code = "Validation.EnvironmentConfigInvalid";

    private static readonly Regex EntryPath = new(@"^\$\.environments\[(\d+)\](?:\.(.+))?$", RegexOptions.CultureInvariant);

    private EnvironmentConfigFileException(string message, string filePath, string? entry, string? field,
        IReadOnlyList<string>? acceptedValues, JsonException inner)
        : base(message, inner.Path, inner.LineNumber, inner.BytePositionInLine, inner)
    {
        FilePath = filePath;
        Entry = entry;
        Field = field;
        AcceptedValues = acceptedValues;
    }

    /// <summary>The error code: <see cref="Code"/>.</summary>
    public string ErrorCode => Code;

    /// <summary>The full path of the environments file.</summary>
    public string FilePath { get; }

    /// <summary>The entry that holds the value: its URL, else its label, else its position. Null when the failure is outside an entry.</summary>
    public string? Entry { get; }

    /// <summary>The field, as written in the file (for example <c>type</c> or <c>safety_settings.cross_env_dml_policy</c>). Null when not known.</summary>
    public string? Field { get; }

    /// <summary>The values the field accepts, when the failure is a value that names no member.</summary>
    public IReadOnlyList<string>? AcceptedValues { get; }

    /// <summary>Builds the exception for a failed read of <paramref name="filePath"/> whose content is <paramref name="json"/>.</summary>
    internal static EnvironmentConfigFileException From(string filePath, string json, JsonException failure)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        string? entry = null;
        string? field = null;
        var match = failure.Path is null ? Match.Empty : EntryPath.Match(failure.Path);
        if (match.Success)
        {
            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            entry = DescribeEntry(json, index);
            field = match.Groups[2].Success ? match.Groups[2].Value : null;
        }
        else if (failure.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal))
        {
            field = path[2..];
        }

        var accepted = (failure as EnumNameJsonException)?.AcceptedValues;
        string message;
        if (accepted is not null)
        {
            message =
                $"The environment configuration file '{fullPath}' cannot be read: " +
                (entry is null ? $"the field '{field ?? "?"}'" : $"in the entry for {entry}, the field '{field ?? "?"}'") +
                $" holds a value that is not one of its names. Accepted values, written as text in double quotes: {string.Join(", ", accepted)}. " +
                "Correct that field in the file by hand, then run the command again.";
        }
        else
        {
            var line = failure.LineNumber is { } n ? $" at line {n + 1}" : string.Empty;
            message =
                $"The environment configuration file '{fullPath}' cannot be read: it is not valid JSON{line}" +
                (entry is null ? string.Empty : $", in the entry for {entry}") +
                (field is null ? string.Empty : $", near the field '{field}'") +
                ". Correct the file by hand, then run the command again.";
        }

        message += " Until then every PPDS command that reads this file stops, so that no write runs with a protection PPDS cannot establish.";
        return new EnvironmentConfigFileException(message, fullPath, entry, field, accepted, failure);
    }

    /// <summary>The entry's URL, else its label, else its position, read from the file's text.</summary>
    private static string DescribeEntry(string json, int index)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("environments", out var environments)
                && environments.ValueKind == JsonValueKind.Array
                && index < environments.GetArrayLength())
            {
                var element = environments[index];
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(url.GetString()))
                        return $"'{url.GetString()}'";
                    if (element.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(label.GetString()))
                        return $"the label '{label.GetString()}'";
                }
            }
        }
        catch (JsonException)
        {
            // The text is not valid JSON further on; the position still identifies the entry.
        }

        return $"environment number {index + 1} in the list";
    }
}
