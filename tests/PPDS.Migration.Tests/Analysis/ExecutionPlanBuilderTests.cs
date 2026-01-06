using FluentAssertions;
using PPDS.Migration.Analysis;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Analysis;

public class ExecutionPlanBuilderTests
{
    [Fact]
    public void Build_ThrowsWhenGraphIsNull()
    {
        var builder = new ExecutionPlanBuilder();
        var schema = new MigrationSchema();

        var act = () => builder.Build(null!, schema);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_ThrowsWhenSchemaIsNull()
    {
        var builder = new ExecutionPlanBuilder();
        var graph = new DependencyGraph();

        var act = () => builder.Build(graph, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_CreatesImportTiers()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "account" },
                new List<string> { "contact" },
                new List<string> { "opportunity" }
            },
            CircularReferences = new List<CircularReference>()
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new() { LogicalName = "account", Fields = new List<FieldSchema>() },
                new() { LogicalName = "contact", Fields = new List<FieldSchema>() },
                new() { LogicalName = "opportunity", Fields = new List<FieldSchema>() }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.Tiers.Should().HaveCount(3);
        plan.Tiers[0].TierNumber.Should().Be(0);
        plan.Tiers[0].Entities.Should().Contain("account");
        plan.Tiers[1].TierNumber.Should().Be(1);
        plan.Tiers[1].Entities.Should().Contain("contact");
        plan.Tiers[2].TierNumber.Should().Be(2);
        plan.Tiers[2].Entities.Should().Contain("opportunity");
    }

    [Fact]
    public void Build_IdentifiesDeferredFields()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "account", "contact" }
            },
            CircularReferences = new List<CircularReference>
            {
                new()
                {
                    Entities = new List<string> { "account", "contact" },
                    Edges = new List<DependencyEdge>
                    {
                        new() { FromEntity = "account", ToEntity = "contact", FieldName = "primarycontactid" },
                        new() { FromEntity = "contact", ToEntity = "account", FieldName = "parentcustomerid" }
                    }
                }
            }
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "account",
                    Fields = new List<FieldSchema>
                    {
                        new() { LogicalName = "primarycontactid", Type = "lookup", LookupEntity = "contact" }
                    }
                },
                new()
                {
                    LogicalName = "contact",
                    Fields = new List<FieldSchema>
                    {
                        new() { LogicalName = "parentcustomerid", Type = "lookup", LookupEntity = "account" }
                    }
                }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.DeferredFields.Should().NotBeEmpty();
    }

    [Fact]
    public void Build_IdentifiesSelfReferencingDeferredFields()
    {
        // Self-referencing entities (e.g., et_source.et_parentsourceid -> et_source) must defer
        // the lookup field because parent and child records may be in the same batch
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "et_source" }
            },
            CircularReferences = new List<CircularReference>
            {
                new()
                {
                    Entities = new List<string> { "et_source" },
                    Edges = new List<DependencyEdge>
                    {
                        new() { FromEntity = "et_source", ToEntity = "et_source", FieldName = "et_parentsourceid" }
                    }
                }
            }
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "et_source",
                    Fields = new List<FieldSchema>
                    {
                        new() { LogicalName = "et_parentsourceid", Type = "lookup", LookupEntity = "et_source" }
                    }
                }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.DeferredFields.Should().ContainKey("et_source");
        plan.DeferredFields["et_source"].Should().Contain("et_parentsourceid");
    }

    [Fact]
    public void Build_IdentifiesManyToManyRelationships()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "systemuser" },
                new List<string> { "role" }
            },
            CircularReferences = new List<CircularReference>()
        };

        var m2mRel = new RelationshipSchema
        {
            Name = "systemuserroles_association",
            IsManyToMany = true,
            Entity1 = "systemuser",
            Entity2 = "role"
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new()
                {
                    LogicalName = "systemuser",
                    Fields = new List<FieldSchema>(),
                    Relationships = new List<RelationshipSchema> { m2mRel }
                },
                new()
                {
                    LogicalName = "role",
                    Fields = new List<FieldSchema>(),
                    Relationships = new List<RelationshipSchema> { m2mRel }
                }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.ManyToManyRelationships.Should().HaveCount(1);
        plan.ManyToManyRelationships[0].Name.Should().Be("systemuserroles_association");
    }

    [Fact]
    public void Build_MarksTiersWithCircularReferences()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "account", "contact" }
            },
            CircularReferences = new List<CircularReference>
            {
                new()
                {
                    Entities = new List<string> { "account", "contact" },
                    Edges = new List<DependencyEdge>()
                }
            }
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new() { LogicalName = "account", Fields = new List<FieldSchema>() },
                new() { LogicalName = "contact", Fields = new List<FieldSchema>() }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.Tiers[0].HasCircularReferences.Should().BeTrue();
    }

    [Fact]
    public void Build_AllTiersRequireWaitByDefault()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>
            {
                new List<string> { "account" },
                new List<string> { "contact" }
            },
            CircularReferences = new List<CircularReference>()
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>
            {
                new() { LogicalName = "account", Fields = new List<FieldSchema>() },
                new() { LogicalName = "contact", Fields = new List<FieldSchema>() }
            }
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.Tiers.Should().AllSatisfy(tier => tier.RequiresWait.Should().BeTrue());
    }

    [Fact]
    public void Build_HandlesEmptyGraph()
    {
        var graph = new DependencyGraph
        {
            Tiers = new List<IReadOnlyList<string>>(),
            CircularReferences = new List<CircularReference>()
        };

        var schema = new MigrationSchema
        {
            Entities = new List<EntitySchema>()
        };

        var builder = new ExecutionPlanBuilder();

        var plan = builder.Build(graph, schema);

        plan.Tiers.Should().BeEmpty();
        plan.DeferredFields.Should().BeEmpty();
        plan.ManyToManyRelationships.Should().BeEmpty();
    }
}
