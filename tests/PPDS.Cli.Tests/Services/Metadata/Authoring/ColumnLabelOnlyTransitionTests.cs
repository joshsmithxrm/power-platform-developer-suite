using System.Reflection;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

// Common BEFORE/fixed contract. Calls only the existing public service with strict synthetic SDK leases.
// The initial B3 run must compile and execute these same bytes; no new production helper is referenced.
[Trait("Category", "Unit")]
public sealed class ColumnLabelOnlyTransitionTests(ITestOutputHelper output)
{
    private static readonly string[] Shapes = ["state", "status", "guid-primary", "guid-concrete", "guid-base"];
    private static readonly string[] LabelModes = ["display", "description", "both", "clear-description"];
    private static readonly string[] Settings = ["RequiredLevel", "MaxLength", "MinValue", "MaxValue", "Precision", "Format",
        "IsAuditEnabled", "IsSecured", "IsValidForAdvancedFind", "AutoNumberFormat"];
    private static readonly string[] IdentityFaults = ["missing-entity", "foreign-entity", "missing-column", "foreign-column",
        "missing-schema", "invalid-schema", "missing-id", "empty-id", "wrong-type", "wrong-type-name"];
    private static readonly string[] Permissions = ["customizable-false-display", "customizable-false-description",
        "renameable-false-display", "renameable-false-description", "unknown-editability", "managed-record-readonly"];
    private static readonly string[] CancelPoints = ["entry", "attribute-return", "updating-reporter", "sdk-update"];

    public static IEnumerable<object[]> ShapesAndLabels() => Product(Shapes, LabelModes);
    public static IEnumerable<object[]> ShapesAndSettings() => Product(Shapes, Settings);
    public static IEnumerable<object[]> ShapesAndIdentityFaults() => Product(Shapes, IdentityFaults);
    public static IEnumerable<object[]> ShapesAndPermissions() => Product(Shapes, Permissions);
    public static IEnumerable<object[]> ShapesAndCancellation() => Product(Shapes, CancelPoints);
    public static IEnumerable<object[]> AllShapes() => Shapes.Select(shape => new object[] { shape });
    private static IEnumerable<object[]> Product(string[] left, string[] right)
        => left.SelectMany(first => right.Select(second => new object[] { first, second }));

    [Theory, MemberData(nameof(ShapesAndLabels)), Trait("AC", "AC-123")]
    public async Task NewShapes_SendExactFreshRequestedLabels(string shape, string labelMode)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var original = h.SourceXml();
        var request = Request(labelMode, publish: labelMode == "both");
        request.EntityLogicalName = ColumnLabelHarness.Table.ToUpperInvariant();
        request.ColumnLogicalName = ColumnLabelHarness.Column.ToUpperInvariant();
        await h.UpdateAsync(request);
        var update = Assert.Single(h.Requests.OfType<UpdateAttributeRequest>());
        Assert.Equal(ColumnLabelHarness.Table, update.EntityName);
        Assert.Equal("FixtureSolution", update.SolutionUniqueName);
        Assert.True(update.MergeLabels);
        Assert.NotSame(h.Attribute, update.Attribute);
        Assert.Equal(h.Attribute.GetType(), update.Attribute.GetType());
        Assert.Equal(ColumnLabelHarness.MetadataId, update.Attribute.MetadataId);
        var expected = ColumnLabelHarness.Fresh(shape);
        expected.LogicalName = ColumnLabelHarness.Column;
        expected.SchemaName = ColumnLabelHarness.Schema;
        expected.MetadataId = ColumnLabelHarness.MetadataId;
        RequestedLabels(expected, request);
        Assert.True(XNode.DeepEquals(ColumnLabelHarness.Serialize(expected), ColumnLabelHarness.Serialize(update.Attribute)));
        Assert.Equal(original, h.SourceXml());
        AssertRequestedLabels(update.Attribute, request);
        h.AssertAcknowledged(request.Publish, tableRead: shape == "guid-primary");
    }

    [Theory, MemberData(nameof(ShapesAndSettings)), Trait("AC", "AC-124")]
    public async Task NewShapes_RejectEverySuppliedNonlabelSetting(string shape, string setting)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var request = Request("both", publish: true);
        object value = setting switch
        {
            "RequiredLevel" or "Format" or "AutoNumberFormat" => "",
            "IsAuditEnabled" or "IsSecured" or "IsValidForAdvancedFind" => false,
            "MinValue" or "MaxValue" => 0d,
            _ => 0
        };
        typeof(UpdateColumnRequest).GetProperty(setting)!.SetValue(request, value);
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(request));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("description", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unsupported attribute type", error.Message, StringComparison.OrdinalIgnoreCase);
        h.AssertNoMutation(attributeReadOnly: true);
    }

    [Theory, MemberData(nameof(AllShapes)), Trait("AC", "AC-124")]
    public async Task NewShapes_RequireAtLeastOneLabel(string shape)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var request = Request("none", publish: true);
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(request));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("display", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("description", error.Message, StringComparison.OrdinalIgnoreCase);
        h.AssertNoMutation(attributeReadOnly: true);
    }

    [Theory, MemberData(nameof(ShapesAndIdentityFaults)), Trait("AC", "AC-125")]
    public async Task NewShapes_RejectUncertainTargetIdentity(string shape, string fault)
    {
        using var h = new ColumnLabelHarness(shape, output);
        string expectedField;
        switch (fault)
        {
            case "missing-entity": ColumnLabelHarness.Set(h.Attribute, "EntityLogicalName", null); expectedField = "EntityLogicalName"; break;
            case "foreign-entity": ColumnLabelHarness.Set(h.Attribute, "EntityLogicalName", "foreign_table"); expectedField = "EntityLogicalName"; break;
            case "missing-column": h.Attribute.LogicalName = null; expectedField = "ColumnLogicalName"; break;
            case "foreign-column": h.Attribute.LogicalName = "new_othercolumn"; expectedField = "ColumnLogicalName"; break;
            case "missing-schema": h.Attribute.SchemaName = " "; expectedField = "SchemaName"; break;
            case "invalid-schema": h.Attribute.SchemaName = "invalid schema"; expectedField = "SchemaName"; break;
            case "missing-id": h.Attribute.MetadataId = null; expectedField = "MetadataId"; break;
            case "empty-id": h.Attribute.MetadataId = Guid.Empty; expectedField = "MetadataId"; break;
            case "wrong-type": ColumnLabelHarness.Set(h.Attribute, "AttributeType", AttributeTypeCode.String); expectedField = "ColumnType"; break;
            case "wrong-type-name": ColumnLabelHarness.Set(h.Attribute, "AttributeTypeName", AttributeTypeDisplayName.StringType); expectedField = "ColumnType"; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var original = h.SourceXml();
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Equal(expectedField, error.Field);
        Assert.DoesNotContain("Unsupported attribute type", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, h.SourceXml());
        h.AssertNoMutation(attributeReadOnly: true);
    }

    [Theory, MemberData(nameof(ShapesAndPermissions)), Trait("AC", "AC-126")]
    public async Task NewShapes_ApplyKnownDenialAndUnknownPolicy(string shape, string policy)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var request = Request(policy.EndsWith("description", StringComparison.Ordinal) ? "description" : "display");
        var denied = policy.StartsWith("customizable-false", StringComparison.Ordinal) || policy == "renameable-false-display";
        if (policy.StartsWith("customizable-false", StringComparison.Ordinal)) h.Attribute.IsCustomizable = new BooleanManagedProperty(false);
        if (policy.StartsWith("renameable-false", StringComparison.Ordinal)) h.Attribute.IsRenameable = new BooleanManagedProperty(false);
        if (policy == "unknown-editability") { h.Attribute.IsCustomizable = null; h.Attribute.IsRenameable = null; }
        ColumnLabelHarness.Set(h.Attribute, "IsManaged", true);
        ColumnLabelHarness.Set(h.Attribute, "IsValidForUpdate", false);
        var original = h.SourceXml();
        if (denied)
        {
            var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(request));
            Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
            Assert.Contains(policy.StartsWith("customizable", StringComparison.Ordinal) ? "customiz" : "renam", error.Message, StringComparison.OrdinalIgnoreCase);
            h.AssertNoMutation(attributeReadOnly: true);
        }
        else
        {
            await h.UpdateAsync(request);
            Assert.True(Assert.Single(h.Requests.OfType<UpdateAttributeRequest>()).MergeLabels);
            h.AssertAcknowledged(false, tableRead: shape == "guid-primary");
        }
        Assert.Equal(original, h.SourceXml());
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("missing-key")]
    [InlineData("missing-entity")]
    [InlineData("foreign-entity")]
    [Trait("AC", "AC-125")]
    public async Task ConcreteGuidPrimary_RequiresAuthoritativeTableIdentity(string fault)
    {
        using var h = new ColumnLabelHarness("guid-primary", output);
        if (fault == "wrong-key") ColumnLabelHarness.Set(h.Entity!, "PrimaryIdAttribute", "address1_addressid");
        if (fault == "missing-key") ColumnLabelHarness.Set(h.Entity!, "PrimaryIdAttribute", null);
        if (fault == "missing-entity") h.Entity = null;
        if (fault == "foreign-entity") h.Entity!.LogicalName = "foreign_table";
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("primary", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "RetrieveAttribute", "RetrieveEntity" }, h.Requests.Select(r => r.RequestName));
        h.AssertNoMutation();
    }

    [Theory, MemberData(nameof(ShapesAndCancellation)), Trait("AC", "AC-127")]
    public async Task NewShapes_PropagateCancellationAndReleaseLeases(string shape, string point)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var expected = new OperationCanceledException(h.Token);
        if (point == "entry") h.Cancel();
        if (point == "attribute-return") h.AfterAcknowledgement = name => { if (name == "RetrieveAttribute") h.Cancel(); };
        if (point == "sdk-update") { h.FaultOperation = "UpdateAttribute"; h.Fault = expected; }
        var reporter = point == "updating-reporter" ? new PhaseCancellation(h) : null;
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.UpdateAsync(Request("both", publish: true), reporter));
        Assert.Equal(h.Token, failure.CancellationToken);
        if (point == "sdk-update") Assert.Same(expected, failure);
        if (point == "entry") Assert.Empty(h.Requests);
        if (point != "sdk-update") Assert.DoesNotContain(h.Requests, r => r is UpdateAttributeRequest);
        Assert.DoesNotContain(h.Requests, r => r is PublishXmlRequest);
        Assert.Equal(0, h.CacheInvalidations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-127")]
    public async Task PrimaryGuid_TableCancellationStopsBeforeUpdate(bool sdkThrows)
    {
        using var h = new ColumnLabelHarness("guid-primary", output);
        var original = new OperationCanceledException(h.Token);
        if (sdkThrows) { h.FaultOperation = "RetrieveEntity"; h.Fault = original; }
        else h.AfterAcknowledgement = name => { if (name == "RetrieveEntity") h.Cancel(); };
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.Equal(h.Token, failure.CancellationToken);
        if (sdkThrows) Assert.Same(original, failure);
        Assert.Equal(new[] { "RetrieveAttribute", "RetrieveEntity" }, h.Requests.Select(r => r.RequestName));
        h.AssertNoMutation();
    }

    [Theory]
    [InlineData("RetrieveAttribute")]
    [InlineData("RetrieveEntity")]
    [InlineData("UpdateAttribute")]
    [InlineData("PublishXml")]
    [Trait("AC", "AC-127")]
    public async Task UnexpectedBoundaryFault_IsPrivateAndStops(string operation)
    {
        using var h = new ColumnLabelHarness(operation == "RetrieveEntity" ? "guid-primary" : "state", output);
        var fault = new InvalidOperationException("fixture-sensitive-marker=never-public");
        h.FaultOperation = operation; h.Fault = fault;
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, error.ErrorCode);
        Assert.Same(fault, error.InnerException);
        Assert.DoesNotContain("fixture-sensitive-marker", error.Message, StringComparison.Ordinal);
        Assert.Equal(operation == "PublishXml" ? 1 : 0, h.CacheInvalidations);
        Assert.Equal(operation, h.Requests.Last().RequestName);
    }

    [Theory]
    [InlineData("RetrieveAttribute")]
    [InlineData("UpdateAttribute")]
    [InlineData("PublishXml")]
    [Trait("AC", "AC-127")]
    public async Task CodedFaults_ArePreserved(string operation)
    {
        using var h = new ColumnLabelHarness("state", output);
        var fault = new PpdsException("Fixture.Coded", "Fixed synthetic failure.");
        h.FaultOperation = operation; h.Fault = fault;
        Assert.Same(fault, await Record.ExceptionAsync(() => h.UpdateAsync(Request("display", publish: true))));
        Assert.Equal(operation == "PublishXml" ? 1 : 0, h.CacheInvalidations);
        Assert.Equal(operation, h.Requests.Last().RequestName);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [Trait("AC", "AC-127")]
    public async Task CheckoutFaults_ArePreservedWithoutLeakingSlots(int checkout, bool cancel)
    {
        using var h = new ColumnLabelHarness("state", output);
        Exception fault = cancel ? new OperationCanceledException(h.Token) : new PpdsException("Fixture.Pool", "Fixed synthetic pool failure.");
        h.FaultCheckout = checkout; h.CheckoutFault = fault;
        Assert.Same(fault, await Record.ExceptionAsync(() => h.UpdateAsync(Request("display", publish: true))));
        Assert.Equal(checkout, h.CheckoutAttempts);
        Assert.Equal(checkout - 1, h.Checkouts);
        h.AssertNoMutation();
    }

    [Theory]
    [InlineData("wrong-response")]
    [InlineData("missing-metadata")]
    [Trait("AC", "AC-125")]
    public async Task UnreadableAttributeResponse_IsCodedAndCannotWrite(string fault)
    {
        using var h = new ColumnLabelHarness("state", output) { AttributeResponseFault = fault };
        var error = await Record.ExceptionAsync(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.True(error is PpdsException { ErrorCode: MetadataErrorCodes.SdkOperationFailed }
            or MetadataValidationException { ErrorCode: MetadataErrorCodes.InvalidConstraint }, "Unavailable metadata must produce a coded refusal.");
        h.AssertNoMutation(attributeReadOnly: true);
    }

    [Fact, Trait("AC", "AC-127")]
    public async Task UpdateAck_CancellationStopsPublishAndInvalidatesKnownAppliedCache()
    {
        using var h = new ColumnLabelHarness("state", output);
        h.AfterAcknowledgement = name => { if (name == "UpdateAttribute") h.Cancel(); };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.UpdateAsync(Request("display", publish: true)));
        Assert.Equal(h.Token, error.CancellationToken);
        Assert.Single(h.Requests.OfType<UpdateAttributeRequest>());
        Assert.DoesNotContain(h.Requests, r => r is PublishXmlRequest);
        Assert.Equal(1, h.CacheInvalidations);
    }

    public static IEnumerable<object[]> GenericTypes() => new[] { "string", "memo", "integer", "bigint", "decimal", "double", "money",
        "boolean", "datetime", "multiselect", "picklist", "image", "file", "lookup" }.Select(type => new object[] { type });

    [Theory, MemberData(nameof(GenericTypes)), Trait("AC", "AC-128")]
    public async Task ExistingFourteenTypes_KeepFreshPayloadAndMergeContract(string type)
    {
        using var h = new ColumnLabelHarness(type, output);
        var original = h.SourceXml();
        var request = Request("both");
        await h.UpdateAsync(request);
        var update = Assert.Single(h.Requests.OfType<UpdateAttributeRequest>());
        Assert.Equal(new UpdateAttributeRequest().MergeLabels, update.MergeLabels);
        var expected = ColumnLabelHarness.Fresh(type);
        expected.LogicalName = ColumnLabelHarness.Column; expected.SchemaName = ColumnLabelHarness.Schema;
        RequestedLabels(expected, request);
        Assert.True(XNode.DeepEquals(ColumnLabelHarness.Serialize(expected), ColumnLabelHarness.Serialize(update.Attribute)));
        Assert.Equal(original, h.SourceXml());
        h.AssertAcknowledged(false, tableRead: false);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("description")]
    [InlineData("both")]
    [InlineData("clear-description")]
    [Trait("AC", "AC-128")]
    public async Task ExistingBasePrimary_PreservesAuthoritativeProofAndPayload(string mode)
    {
        using var h = new ColumnLabelHarness("legacy-primary", output);
        var request = Request(mode);
        await h.UpdateAsync(request);
        var update = Assert.Single(h.Requests.OfType<UpdateAttributeRequest>());
        var expected = new AttributeMetadata { LogicalName = ColumnLabelHarness.Column, SchemaName = ColumnLabelHarness.Schema };
        RequestedLabels(expected, request);
        Assert.True(update.MergeLabels); Assert.Null(update.Attribute.MetadataId);
        Assert.True(XNode.DeepEquals(ColumnLabelHarness.Serialize(expected), ColumnLabelHarness.Serialize(update.Attribute)));
        h.AssertAcknowledged(false, tableRead: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-129")]
    public async Task GuardAndDryRun_DoNotReadMetadata(bool dryRun)
    {
        using var h = new ColumnLabelHarness("state", output, activeGuard: !dryRun);
        var request = Request("display", publish: true); request.DryRun = dryRun; request.MaxLength = 0;
        if (dryRun) await h.UpdateAsync(request);
        else Assert.Equal(ErrorCodes.Safety.ShakedownActive, (await Assert.ThrowsAsync<PpdsException>(() => h.UpdateAsync(request))).ErrorCode);
        Assert.Empty(h.Requests); Assert.Equal(0, h.Checkouts); Assert.Equal(0, h.CacheInvalidations);
    }

    [Theory]
    [InlineData("entity-name")]
    [InlineData("managed-property")]
    [InlineData("party-list")]
    [InlineData("calendar-rules")]
    [InlineData("virtual")]
    [InlineData("base-null-type")]
    [InlineData("guid-base-unknown-primary")]
    [InlineData("guid-concrete-unknown-primary")]
    [Trait("AC", "AC-129")]
    public async Task UnreviewedShapes_StayUnsupported(string shape)
    {
        using var h = new ColumnLabelHarness(shape, output);
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.UpdateAsync(Request("both", publish: true)));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("type", error.Message, StringComparison.OrdinalIgnoreCase);
        h.AssertNoMutation(attributeReadOnly: true);
    }

    [Fact, Trait("AC", "AC-130")]
    public void PublicRequestAndServiceContracts_RemainCompatible()
    {
        var constructor = Assert.Single(typeof(DataverseMetadataAuthoringService).GetConstructors());
        Assert.Equal(5, constructor.GetParameters().Length);
        var method = typeof(IMetadataAuthoringService).GetMethod(nameof(IMetadataAuthoringService.UpdateColumnAsync))!;
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Equal(new[] { typeof(UpdateColumnRequest), typeof(IMetadataAuthoringProgressReporter), typeof(CancellationToken) }, method.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(new[] { "AutoNumberFormat", "ColumnLogicalName", "Description", "DisplayName", "DryRun", "EntityLogicalName", "Format", "IsAuditEnabled",
            "IsSecured", "IsValidForAdvancedFind", "MaxLength", "MaxValue", "MinValue", "Precision", "Publish", "RequiredLevel", "SolutionUniqueName" },
            typeof(UpdateColumnRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private static UpdateColumnRequest Request(string mode, bool publish = false) => new()
    {
        EntityLogicalName = ColumnLabelHarness.Table, ColumnLogicalName = ColumnLabelHarness.Column, SolutionUniqueName = "FixtureSolution",
        DisplayName = mode is "display" or "both" ? "Requested label" : null,
        Description = mode is "description" or "both" ? "Requested description" : mode == "clear-description" ? "" : null, Publish = publish
    };

    private static void RequestedLabels(AttributeMetadata metadata, UpdateColumnRequest request)
    {
        if (request.DisplayName != null) metadata.DisplayName = new Label(request.DisplayName, 1033);
        if (request.Description != null) metadata.Description = new Label(request.Description, 1033);
    }

    private static void AssertRequestedLabels(AttributeMetadata metadata, UpdateColumnRequest request)
    {
        foreach (var pair in new[] { (metadata.DisplayName, request.DisplayName), (metadata.Description, request.Description) })
        {
            if (pair.Item2 == null) Assert.Null(pair.Item1);
            else
            {
                Assert.NotNull(pair.Item1);
                var label = Assert.Single(pair.Item1.LocalizedLabels);
                Assert.Equal(1033, label.LanguageCode); Assert.Equal(pair.Item2, label.Label);
            }
        }
    }

    private sealed class PhaseCancellation(ColumnLabelHarness harness) : IMetadataAuthoringProgressReporter
    {
        public void ReportPhase(string phase, string? detail = null) { if (phase == "Updating column") harness.Cancel(); }
        public void ReportInfo(string message) { }
    }
}
