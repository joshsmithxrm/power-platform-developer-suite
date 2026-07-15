using System;
using System.Linq;
using FluentAssertions;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Dataverse.Metadata.Models;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Guards the attribute wire contract (#1369): the daemon RPC layer originally
/// forwarded a curated subset of <see cref="AttributeMetadataDto"/> and silently
/// dropped the rest (including AttributeOf, #1368). These tests pin full-fidelity
/// pass-through so a field added to the service DTO cannot silently vanish at the
/// RPC boundary again.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAttributeRpcMappingTests
{
    private static AttributeMetadataDto CreateFullServiceDto() => new()
    {
        MetadataId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        LogicalName = "primarycontactidname",
        DisplayName = "Primary Contact Name",
        SchemaName = "PrimaryContactIdName",
        AttributeType = "String",
        AttributeTypeName = "StringType",
        IsCustomAttribute = false,
        IsManaged = true,
        IsPrimaryId = false,
        IsPrimaryName = false,
        RequiredLevel = "None",
        IsValidForCreate = true,
        IsValidForUpdate = false,
        IsValidForRead = true,
        IsSearchable = true,
        IsFilterable = true,
        IsSortable = true,
        Description = "Auxiliary name column",
        MaxLength = 160,
        MinValue = 1m,
        MaxValue = 100m,
        Precision = 2,
        Targets = ["contact"],
        OptionSetName = "some_optionset",
        IsGlobalOptionSet = true,
        DateTimeBehavior = "UserLocal",
        Format = "Text",
        SourceType = 1,
        IsSecured = true,
        FormulaDefinition = "<formula/>",
        AutoNumberFormat = "AUTO-{SEQNUM:4}",
        IsValidForForm = true,
        IsValidForGrid = false,
        CanBeSecuredForRead = true,
        CanBeSecuredForCreate = false,
        CanBeSecuredForUpdate = true,
        IsRetrievable = true,
        AttributeOf = "primarycontactid",
        IsLogical = true,
        IntroducedVersion = "5.0.0.0",
        DeprecatedVersion = "9.0.0.0",
        IsAuditEnabled = true,
        IsCustomizable = false,
        IsRenameable = true,
        IsValidForAdvancedFind = true,
        ExternalName = "ext_name",
        ColumnNumber = 42,
        CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc),
    };

    [Fact]
    public void MapAttributeToRpc_PreservesFullFidelityFields()
    {
        var rpc = RpcMethodHandler.MapAttributeToRpc(CreateFullServiceDto());

        // #1368: the deterministic auxiliary marker must cross the wire.
        rpc.AttributeOf.Should().Be("primarycontactid");

        // #1369: previously-dropped fields.
        rpc.MetadataId.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        rpc.IsManaged.Should().BeTrue();
        rpc.IsLogical.Should().BeTrue();
        rpc.IsValidForCreate.Should().BeTrue();
        rpc.IsValidForUpdate.Should().BeFalse();
        rpc.IsValidForRead.Should().BeTrue();
        rpc.IsValidForForm.Should().BeTrue();
        rpc.IsValidForGrid.Should().BeFalse();
        rpc.IsValidForAdvancedFind.Should().BeTrue();
        rpc.IsSearchable.Should().BeTrue();
        rpc.IsFilterable.Should().BeTrue();
        rpc.IsSortable.Should().BeTrue();
        rpc.IsRetrievable.Should().BeTrue();
        rpc.CanBeSecuredForRead.Should().BeTrue();
        rpc.CanBeSecuredForCreate.Should().BeFalse();
        rpc.CanBeSecuredForUpdate.Should().BeTrue();
        rpc.IsAuditEnabled.Should().BeTrue();
        rpc.IsCustomizable.Should().BeFalse();
        rpc.IsRenameable.Should().BeTrue();
        rpc.FormulaDefinition.Should().Be("<formula/>");
        rpc.IntroducedVersion.Should().Be("5.0.0.0");
        rpc.DeprecatedVersion.Should().Be("9.0.0.0");
        rpc.ExternalName.Should().Be("ext_name");
        rpc.ColumnNumber.Should().Be(42);
        rpc.CreatedOn.Should().Be(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        rpc.ModifiedOn.Should().Be(new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));

        // Curated fields keep working.
        rpc.LogicalName.Should().Be("primarycontactidname");
        rpc.MaxLength.Should().Be(160);
        rpc.IsSecured.Should().BeTrue();
    }

    [Fact]
    public void RpcAttributeDto_CoversEveryServiceDtoProperty()
    {
        // Completeness guard: every public property on the service DTO must have a
        // same-named counterpart on the RPC DTO. This is the exact drift that caused
        // #1369 — the RPC layer narrowing the contract without anyone noticing.
        var serviceProps = typeof(AttributeMetadataDto).GetProperties().Select(p => p.Name);
        var rpcProps = typeof(MetadataAttributeDto).GetProperties().Select(p => p.Name).ToHashSet();

        var missing = serviceProps.Where(name => !rpcProps.Contains(name)).ToList();

        missing.Should().BeEmpty(
            "the RPC MetadataAttributeDto must mirror every AttributeMetadataDto property; " +
            "add the field to the RPC DTO and MapAttributeToRpc");
    }
}
