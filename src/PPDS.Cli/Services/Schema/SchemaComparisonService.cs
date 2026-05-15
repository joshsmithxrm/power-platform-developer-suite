using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Cli.Services.Schema.Models;
using PPDS.Cli.Services.Schema.Snapshots;

namespace PPDS.Cli.Services.Schema;

/// <summary>
/// Pure, I/O-free implementation of <see cref="ISchemaComparisonService"/>.
/// </summary>
public sealed class SchemaComparisonService : ISchemaComparisonService
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    // Required-level ordering: higher = stricter. Used to decide if target became more demanding.
    private static readonly Dictionary<string, int> RequiredLevelRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0,
        ["Recommended"] = 1,
        ["ApplicationRequired"] = 2,
        ["SystemRequired"] = 3
    };

    /// <inheritdoc />
    public SchemaCompareReport Compare(SchemaSnapshot source, SchemaSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var diffs = new List<SchemaDifference>();

        var targetEntities = target.Entities.ToDictionary(e => e.LogicalName, NameComparer);
        var sourceEntities = source.Entities.ToDictionary(e => e.LogicalName, NameComparer);

        foreach (var entity in source.Entities.OrderBy(e => e.LogicalName, NameComparer))
        {
            if (!targetEntities.TryGetValue(entity.LogicalName, out var targetEntity))
            {
                diffs.Add(new SchemaDifference
                {
                    Severity = DiffSeverity.Error,
                    Kind = DiffKind.MissingEntity,
                    Entity = entity.LogicalName,
                    Message = $"Entity '{entity.LogicalName}' exists in source but not in target."
                });
                continue;
            }

            CompareAttributes(entity, targetEntity, source.IncludesOptionSetValues && target.IncludesOptionSetValues, diffs);
            CompareRelationships(entity, targetEntity, diffs);
        }

        // ExtraInTarget — entities present in target but not in source.
        foreach (var entity in target.Entities.OrderBy(e => e.LogicalName, NameComparer))
        {
            if (!sourceEntities.ContainsKey(entity.LogicalName))
            {
                diffs.Add(new SchemaDifference
                {
                    Severity = DiffSeverity.Info,
                    Kind = DiffKind.ExtraEntity,
                    Entity = entity.LogicalName,
                    Message = $"Entity '{entity.LogicalName}' exists in target but not in source."
                });
            }
        }

        return new SchemaCompareReport
        {
            Source = source.Source,
            Target = target.Source,
            Differences = diffs
        };
    }

    private static void CompareAttributes(
        EntitySnapshot source,
        EntitySnapshot target,
        bool compareOptionSetValues,
        List<SchemaDifference> diffs)
    {
        var targetAttrs = target.Attributes.ToDictionary(a => a.LogicalName, NameComparer);
        var sourceAttrs = source.Attributes.ToDictionary(a => a.LogicalName, NameComparer);

        foreach (var attr in source.Attributes.OrderBy(a => a.LogicalName, NameComparer))
        {
            if (!targetAttrs.TryGetValue(attr.LogicalName, out var targetAttr))
            {
                var isRequired =
                    string.Equals(attr.RequiredLevel, "ApplicationRequired", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attr.RequiredLevel, "SystemRequired", StringComparison.OrdinalIgnoreCase);

                diffs.Add(new SchemaDifference
                {
                    Severity = isRequired ? DiffSeverity.Error : DiffSeverity.Warning,
                    Kind = DiffKind.MissingAttribute,
                    Entity = source.LogicalName,
                    Attribute = attr.LogicalName,
                    Message = $"Attribute '{source.LogicalName}.{attr.LogicalName}' exists in source but not in target."
                });
                continue;
            }

            if (!string.Equals(attr.AttributeType, targetAttr.AttributeType, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add(new SchemaDifference
                {
                    Severity = DiffSeverity.Error,
                    Kind = DiffKind.TypeMismatch,
                    Entity = source.LogicalName,
                    Attribute = attr.LogicalName,
                    Message = $"Attribute '{source.LogicalName}.{attr.LogicalName}' type changed: source='{attr.AttributeType}', target='{targetAttr.AttributeType}'.",
                    SourceValue = attr.AttributeType,
                    TargetValue = targetAttr.AttributeType
                });
                continue; // further comparisons would be apples-to-oranges
            }

            CompareRequiredLevel(source.LogicalName, attr, targetAttr, diffs);
            CompareLength(source.LogicalName, attr, targetAttr, diffs);
            ComparePrecision(source.LogicalName, attr, targetAttr, diffs);
            CompareLookupTargets(source.LogicalName, attr, targetAttr, diffs);

            if (compareOptionSetValues)
            {
                CompareOptionValues(source.LogicalName, attr, targetAttr, diffs);
            }
        }

        foreach (var attr in target.Attributes.OrderBy(a => a.LogicalName, NameComparer))
        {
            if (!sourceAttrs.ContainsKey(attr.LogicalName))
            {
                diffs.Add(new SchemaDifference
                {
                    Severity = DiffSeverity.Info,
                    Kind = DiffKind.ExtraAttribute,
                    Entity = target.LogicalName,
                    Attribute = attr.LogicalName,
                    Message = $"Attribute '{target.LogicalName}.{attr.LogicalName}' exists in target but not in source."
                });
            }
        }
    }

    private static void CompareRequiredLevel(
        string entity,
        AttributeSnapshot source,
        AttributeSnapshot target,
        List<SchemaDifference> diffs)
    {
        if (source.RequiredLevel is null || target.RequiredLevel is null)
        {
            return;
        }

        if (!RequiredLevelRank.TryGetValue(source.RequiredLevel, out var sourceRank) ||
            !RequiredLevelRank.TryGetValue(target.RequiredLevel, out var targetRank))
        {
            return;
        }

        if (targetRank > sourceRank)
        {
            diffs.Add(new SchemaDifference
            {
                Severity = DiffSeverity.Error,
                Kind = DiffKind.RequiredLevelStricter,
                Entity = entity,
                Attribute = source.LogicalName,
                Message = $"Attribute '{entity}.{source.LogicalName}' required level is stricter in target: source='{source.RequiredLevel}', target='{target.RequiredLevel}'.",
                SourceValue = source.RequiredLevel,
                TargetValue = target.RequiredLevel
            });
        }
    }

    private static void CompareLength(
        string entity,
        AttributeSnapshot source,
        AttributeSnapshot target,
        List<SchemaDifference> diffs)
    {
        if (source.MaxLength is { } sourceLen && target.MaxLength is { } targetLen && targetLen < sourceLen)
        {
            diffs.Add(new SchemaDifference
            {
                Severity = DiffSeverity.Warning,
                Kind = DiffKind.LengthShrunk,
                Entity = entity,
                Attribute = source.LogicalName,
                Message = $"Attribute '{entity}.{source.LogicalName}' max length shrunk: source={sourceLen}, target={targetLen}. Data may be truncated.",
                SourceValue = sourceLen.ToString(),
                TargetValue = targetLen.ToString()
            });
        }
    }

    private static void ComparePrecision(
        string entity,
        AttributeSnapshot source,
        AttributeSnapshot target,
        List<SchemaDifference> diffs)
    {
        if (source.Precision is { } sourceP && target.Precision is { } targetP && targetP < sourceP)
        {
            diffs.Add(new SchemaDifference
            {
                Severity = DiffSeverity.Warning,
                Kind = DiffKind.PrecisionLoss,
                Entity = entity,
                Attribute = source.LogicalName,
                Message = $"Attribute '{entity}.{source.LogicalName}' precision reduced: source={sourceP}, target={targetP}. Precision may be lost.",
                SourceValue = sourceP.ToString(),
                TargetValue = targetP.ToString()
            });
        }
    }

    private static void CompareLookupTargets(
        string entity,
        AttributeSnapshot source,
        AttributeSnapshot target,
        List<SchemaDifference> diffs)
    {
        if (source.LookupTargets is null || target.LookupTargets is null)
        {
            return;
        }

        var targetSet = new HashSet<string>(target.LookupTargets, NameComparer);
        var missing = source.LookupTargets.Where(t => !targetSet.Contains(t)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        diffs.Add(new SchemaDifference
        {
            Severity = DiffSeverity.Error,
            Kind = DiffKind.LookupTargetMissing,
            Entity = entity,
            Attribute = source.LogicalName,
            Message = $"Lookup '{entity}.{source.LogicalName}' targets missing in target: {string.Join(", ", missing)}.",
            SourceValue = string.Join(",", source.LookupTargets),
            TargetValue = string.Join(",", target.LookupTargets)
        });
    }

    private static void CompareOptionValues(
        string entity,
        AttributeSnapshot source,
        AttributeSnapshot target,
        List<SchemaDifference> diffs)
    {
        if (source.OptionValues is null || target.OptionValues is null)
        {
            return;
        }

        var targetSet = target.OptionValues.ToHashSet();
        foreach (var value in source.OptionValues.Where(v => !targetSet.Contains(v)).OrderBy(v => v))
        {
            diffs.Add(new SchemaDifference
            {
                Severity = DiffSeverity.Warning,
                Kind = DiffKind.MissingOptionValue,
                Entity = entity,
                Attribute = source.LogicalName,
                Message = $"Option-set '{entity}.{source.LogicalName}' value {value} present in source but missing in target.",
                SourceValue = value.ToString(),
                TargetValue = null
            });
        }
    }

    private static void CompareRelationships(
        EntitySnapshot source,
        EntitySnapshot target,
        List<SchemaDifference> diffs)
    {
        var targetRels = new HashSet<string>(target.Relationships.Select(r => r.SchemaName), NameComparer);
        foreach (var rel in source.Relationships.OrderBy(r => r.SchemaName, NameComparer))
        {
            if (!targetRels.Contains(rel.SchemaName))
            {
                diffs.Add(new SchemaDifference
                {
                    Severity = DiffSeverity.Error,
                    Kind = DiffKind.MissingRelationship,
                    Entity = source.LogicalName,
                    Message = $"Relationship '{rel.SchemaName}' ({rel.RelationshipType}) on '{source.LogicalName}' missing in target."
                });
            }
        }
    }
}
