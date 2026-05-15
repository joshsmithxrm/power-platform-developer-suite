using System.Linq;
using FluentAssertions;
using PPDS.Cli.Services.Schema;
using PPDS.Cli.Services.Schema.Models;
using PPDS.Cli.Services.Schema.Snapshots;
using Xunit;

namespace PPDS.Cli.Tests.Services.Schema;

public class SchemaComparisonServiceTests
{
    private readonly SchemaComparisonService _service = new();

    private static SchemaSnapshot Snap(string name, params EntitySnapshot[] entities) =>
        new() { Source = name, Entities = entities, IncludesOptionSetValues = true };

    private static EntitySnapshot Entity(string name, params AttributeSnapshot[] attrs) =>
        new()
        {
            LogicalName = name,
            Attributes = attrs,
            Relationships = System.Array.Empty<RelationshipSnapshot>()
        };

    private static EntitySnapshot EntityWithRels(string name, AttributeSnapshot[] attrs, RelationshipSnapshot[] rels) =>
        new() { LogicalName = name, Attributes = attrs, Relationships = rels };

    private static AttributeSnapshot Attr(
        string name,
        string type = "string",
        string? requiredLevel = "None",
        int? maxLength = null,
        int? precision = null,
        System.Collections.Generic.IReadOnlyList<string>? targets = null,
        System.Collections.Generic.IReadOnlyList<int>? options = null) =>
        new()
        {
            LogicalName = name,
            AttributeType = type,
            RequiredLevel = requiredLevel,
            MaxLength = maxLength,
            Precision = precision,
            LookupTargets = targets,
            OptionValues = options
        };

    [Fact]
    public void Compare_IdenticalSnapshots_ProducesNoDifferences()
    {
        var src = Snap("a", Entity("account", Attr("name", "string", maxLength: 100)));
        var tgt = Snap("b", Entity("account", Attr("name", "string", maxLength: 100)));

        var report = _service.Compare(src, tgt);

        report.Differences.Should().BeEmpty();
        report.HighestSeverity.Should().BeNull();
        report.Summary.Total.Should().Be(0);
    }

    [Fact]
    public void Compare_EntityMissingInTarget_ProducesError()
    {
        var src = Snap("a", Entity("account"), Entity("contact"));
        var tgt = Snap("b", Entity("account"));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.MissingEntity).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
        diff.Entity.Should().Be("contact");
        report.HighestSeverity.Should().Be(DiffSeverity.Error);
    }

    [Fact]
    public void Compare_EntityExtraInTarget_ProducesInfo()
    {
        var src = Snap("a", Entity("account"));
        var tgt = Snap("b", Entity("account"), Entity("contact"));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.ExtraEntity).Subject;
        diff.Severity.Should().Be(DiffSeverity.Info);
        diff.Entity.Should().Be("contact");
    }

    [Fact]
    public void Compare_MissingRequiredAttribute_ProducesError()
    {
        var src = Snap("a", Entity("account", Attr("name", "string", requiredLevel: "ApplicationRequired")));
        var tgt = Snap("b", Entity("account"));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.MissingAttribute).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
        diff.Entity.Should().Be("account");
        diff.Attribute.Should().Be("name");
    }

    [Fact]
    public void Compare_MissingOptionalAttribute_ProducesWarning()
    {
        var src = Snap("a", Entity("account", Attr("nickname", "string", requiredLevel: "None")));
        var tgt = Snap("b", Entity("account"));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.MissingAttribute).Subject;
        diff.Severity.Should().Be(DiffSeverity.Warning);
    }

    [Fact]
    public void Compare_TypeMismatch_ProducesError()
    {
        var src = Snap("a", Entity("account", Attr("age", "integer")));
        var tgt = Snap("b", Entity("account", Attr("age", "string")));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.TypeMismatch).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
        diff.SourceValue.Should().Be("integer");
        diff.TargetValue.Should().Be("string");
    }

    [Fact]
    public void Compare_StringLengthShrunk_ProducesWarning()
    {
        var src = Snap("a", Entity("account", Attr("name", "string", maxLength: 200)));
        var tgt = Snap("b", Entity("account", Attr("name", "string", maxLength: 100)));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.LengthShrunk).Subject;
        diff.Severity.Should().Be(DiffSeverity.Warning);
        diff.SourceValue.Should().Be("200");
        diff.TargetValue.Should().Be("100");
    }

    [Fact]
    public void Compare_StringLengthGrew_ProducesNoDifference()
    {
        // Negative case: target larger than source should NOT trigger a length diff.
        var src = Snap("a", Entity("account", Attr("name", "string", maxLength: 100)));
        var tgt = Snap("b", Entity("account", Attr("name", "string", maxLength: 200)));

        var report = _service.Compare(src, tgt);

        report.Differences.Should().NotContain(d => d.Kind == DiffKind.LengthShrunk);
    }

    [Fact]
    public void Compare_PrecisionReduced_ProducesWarning()
    {
        var src = Snap("a", Entity("account", Attr("amount", "decimal", precision: 4)));
        var tgt = Snap("b", Entity("account", Attr("amount", "decimal", precision: 2)));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.PrecisionLoss).Subject;
        diff.Severity.Should().Be(DiffSeverity.Warning);
    }

    [Fact]
    public void Compare_RequiredLevelStricter_ProducesError()
    {
        var src = Snap("a", Entity("account", Attr("name", "string", requiredLevel: "None")));
        var tgt = Snap("b", Entity("account", Attr("name", "string", requiredLevel: "ApplicationRequired")));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.RequiredLevelStricter).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
    }

    [Fact]
    public void Compare_RequiredLevelLooser_ProducesNoDifference()
    {
        var src = Snap("a", Entity("account", Attr("name", "string", requiredLevel: "ApplicationRequired")));
        var tgt = Snap("b", Entity("account", Attr("name", "string", requiredLevel: "None")));

        var report = _service.Compare(src, tgt);

        report.Differences.Should().NotContain(d => d.Kind == DiffKind.RequiredLevelStricter);
    }

    [Fact]
    public void Compare_LookupTargetMissingInTarget_ProducesError()
    {
        var src = Snap("a", Entity("account", Attr("customer", "lookup", targets: new[] { "contact", "lead" })));
        var tgt = Snap("b", Entity("account", Attr("customer", "lookup", targets: new[] { "contact" })));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.LookupTargetMissing).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
        diff.Message.Should().Contain("lead");
    }

    [Fact]
    public void Compare_MissingOptionValue_ProducesWarning()
    {
        var src = Snap("a", Entity("account", Attr("status", "picklist", options: new[] { 1, 2, 3 })));
        var tgt = Snap("b", Entity("account", Attr("status", "picklist", options: new[] { 1, 2 })));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.MissingOptionValue).Subject;
        diff.Severity.Should().Be(DiffSeverity.Warning);
        diff.SourceValue.Should().Be("3");
    }

    [Fact]
    public void Compare_OptionValuesSkippedWhenSnapshotDoesNotCarryThem()
    {
        // Package mode: source carries no option values; should not emit MissingOptionValue.
        var src = new SchemaSnapshot
        {
            Source = "data:x.zip",
            Entities = new[] { Entity("account", Attr("status", "picklist")) },
            IncludesOptionSetValues = false
        };
        var tgt = Snap("b", Entity("account", Attr("status", "picklist", options: new[] { 1, 2 })));

        var report = _service.Compare(src, tgt);

        report.Differences.Should().NotContain(d => d.Kind == DiffKind.MissingOptionValue);
    }

    [Fact]
    public void Compare_RelationshipMissingInTarget_ProducesError()
    {
        var rel = new RelationshipSnapshot
        {
            SchemaName = "account_contacts",
            RelationshipType = "OneToMany",
            ReferencingEntity = "contact",
            ReferencedEntity = "account"
        };
        var src = Snap("a", EntityWithRels("account", System.Array.Empty<AttributeSnapshot>(), new[] { rel }));
        var tgt = Snap("b", Entity("account"));

        var report = _service.Compare(src, tgt);

        var diff = report.Differences.Should().ContainSingle(d => d.Kind == DiffKind.MissingRelationship).Subject;
        diff.Severity.Should().Be(DiffSeverity.Error);
        diff.Message.Should().Contain("account_contacts");
    }

    [Fact]
    public void Compare_SummaryAndHighestSeverityReflectMixedDiffs()
    {
        var src = Snap("a",
            Entity("account",
                Attr("required", "string", requiredLevel: "ApplicationRequired", maxLength: 100),
                Attr("optional", "string", requiredLevel: "None")),
            Entity("contact"));
        var tgt = Snap("b",
            Entity("account",
                Attr("required", "string", maxLength: 50),
                Attr("optional", "string"),
                Attr("extra", "string"))); // contact missing entirely

        var report = _service.Compare(src, tgt);

        report.Summary.Errors.Should().BeGreaterThan(0);
        report.Summary.Warnings.Should().BeGreaterThan(0);
        report.Summary.Infos.Should().BeGreaterThan(0);
        report.HighestSeverity.Should().Be(DiffSeverity.Error);
    }

    [Fact]
    public void Compare_NamesAreCaseInsensitive()
    {
        var src = Snap("a", Entity("Account", Attr("Name")));
        var tgt = Snap("b", Entity("account", Attr("name")));

        var report = _service.Compare(src, tgt);

        report.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Compare_DifferencesAreInDeterministicOrder()
    {
        var src = Snap("a",
            Entity("zeta"),
            Entity("alpha"),
            Entity("beta"));
        var tgt = Snap("b");

        var report = _service.Compare(src, tgt);

        report.Differences
            .Where(d => d.Kind == DiffKind.MissingEntity)
            .Select(d => d.Entity)
            .Should().ContainInOrder("alpha", "beta", "zeta");
    }
}

public class CompareCommandBuildReportTests
{
    [Fact]
    public void BuildReport_AddsExtraEntityDiffs_ForUnloadedTargetEntities()
    {
        var service = new SchemaComparisonService();
        var source = new SchemaSnapshot
        {
            Source = "data:p.zip",
            Entities = new[]
            {
                new EntitySnapshot
                {
                    LogicalName = "account",
                    Attributes = System.Array.Empty<AttributeSnapshot>(),
                    Relationships = System.Array.Empty<RelationshipSnapshot>()
                }
            },
            IncludesOptionSetValues = false
        };
        var target = new SchemaSnapshot
        {
            Source = "env:qa",
            Entities = new[]
            {
                new EntitySnapshot
                {
                    LogicalName = "account",
                    Attributes = System.Array.Empty<AttributeSnapshot>(),
                    Relationships = System.Array.Empty<RelationshipSnapshot>()
                }
            },
            IncludesOptionSetValues = true
        };

        var report = PPDS.Cli.Commands.Schema.CompareCommand.BuildReport(
            service, source, target, new[] { "contact", "lead" });

        report.Differences
            .Where(d => d.Kind == DiffKind.ExtraEntity)
            .Select(d => d.Entity)
            .Should().BeEquivalentTo(new[] { "contact", "lead" });
        report.Differences.Where(d => d.Kind == DiffKind.ExtraEntity)
            .All(d => d.Severity == DiffSeverity.Info).Should().BeTrue();
    }

    [Fact]
    public void BuildReport_DoesNotDuplicateEntitiesThatExistInSource()
    {
        var service = new SchemaComparisonService();
        var source = new SchemaSnapshot
        {
            Source = "a",
            Entities = new[]
            {
                new EntitySnapshot
                {
                    LogicalName = "contact",
                    Attributes = System.Array.Empty<AttributeSnapshot>(),
                    Relationships = System.Array.Empty<RelationshipSnapshot>()
                }
            }
        };
        var target = new SchemaSnapshot
        {
            Source = "b",
            Entities = new[]
            {
                new EntitySnapshot
                {
                    LogicalName = "contact",
                    Attributes = System.Array.Empty<AttributeSnapshot>(),
                    Relationships = System.Array.Empty<RelationshipSnapshot>()
                }
            }
        };

        // "contact" appears in unloaded list AND in source — should not produce ExtraEntity.
        var report = PPDS.Cli.Commands.Schema.CompareCommand.BuildReport(
            service, source, target, new[] { "contact" });

        report.Differences.Where(d => d.Kind == DiffKind.ExtraEntity).Should().BeEmpty();
    }
}

public class CompareCommandExitCodeTests
{
    [Theory]
    [InlineData(null, 0)]                       // no diffs -> Success
    [InlineData(DiffSeverity.Info, 0)]          // info -> Success
    [InlineData(DiffSeverity.Warning, 1)]       // warning -> PartialSuccess
    [InlineData(DiffSeverity.Error, 8)]         // error -> ValidationError
    public void ExitCodeFor_MapsSeverityCorrectly(DiffSeverity? severity, int expected)
    {
        var actual = PPDS.Cli.Commands.Schema.CompareCommand.ExitCodeFor(severity);
        actual.Should().Be(expected);
    }
}
