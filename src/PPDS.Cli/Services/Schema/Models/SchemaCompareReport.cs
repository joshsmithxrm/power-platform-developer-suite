using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.Schema.Models;

/// <summary>
/// Report produced by comparing two <c>SchemaSnapshot</c> instances.
/// </summary>
public sealed class SchemaCompareReport
{
    /// <summary>Source descriptor (e.g. <c>data:path.zip</c> or <c>env:https://...</c>).</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Target descriptor.</summary>
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    /// <summary>UTC timestamp the comparison was produced.</summary>
    [JsonPropertyName("comparedAt")]
    public DateTime ComparedAt { get; init; } = DateTime.UtcNow;

    /// <summary>All differences in deterministic order (entity, attribute, kind).</summary>
    [JsonPropertyName("differences")]
    public required IReadOnlyList<SchemaDifference> Differences { get; init; }

    /// <summary>Summary counts.</summary>
    [JsonPropertyName("summary")]
    public SchemaCompareSummary Summary => new()
    {
        Errors = Differences.Count(d => d.Severity == DiffSeverity.Error),
        Warnings = Differences.Count(d => d.Severity == DiffSeverity.Warning),
        Infos = Differences.Count(d => d.Severity == DiffSeverity.Info),
        Total = Differences.Count
    };

    /// <summary>Highest severity in the report, or <c>null</c> if no differences.</summary>
    [JsonIgnore]
    public DiffSeverity? HighestSeverity =>
        Differences.Count == 0 ? null : Differences.Max(d => d.Severity);
}

/// <summary>Summary counts for a schema compare report.</summary>
public sealed record SchemaCompareSummary
{
    /// <summary>Number of <see cref="DiffSeverity.Error"/> diffs.</summary>
    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    /// <summary>Number of <see cref="DiffSeverity.Warning"/> diffs.</summary>
    [JsonPropertyName("warnings")]
    public int Warnings { get; init; }

    /// <summary>Number of <see cref="DiffSeverity.Info"/> diffs.</summary>
    [JsonPropertyName("infos")]
    public int Infos { get; init; }

    /// <summary>Total diff count.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }
}
