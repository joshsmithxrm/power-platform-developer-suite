using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata.Authoring;

[Trait("Category", "Unit")]
public class CreateColumnTypeTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly SchemaValidator _validator = new();
    private readonly DataverseMetadataAuthoringService _service;

    public CreateColumnTypeTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        SetupPublisherPrefixQuery("new");

        _service = new DataverseMetadataAuthoringService(_pool.Object, _validator);
    }

    private void SetupPublisherPrefixQuery(string prefix)
    {
        var publisherId = Guid.NewGuid();

        var solutionEntity = new Entity("solution")
        {
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };
        var solutionCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { solutionEntity });

        var publisherEntity = new Entity("publisher")
        {
            ["customizationprefix"] = prefix
        };
        var publisherCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { publisherEntity });

        _client.SetupSequence(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(solutionCollection)
            .ReturnsAsync(publisherCollection);
    }

    private CreateColumnRequest MakeRequest(SchemaColumnType columnType) => new()
    {
        SolutionUniqueName = "TestSolution",
        EntityLogicalName = "account",
        SchemaName = "new_TestField",
        DisplayName = "Test Field",
        ColumnType = columnType
    };

    private async Task<CreateAttributeRequest> CaptureCreateAttributeRequest(CreateColumnRequest request)
    {
        CreateAttributeRequest? captured = null;

        var response = new CreateAttributeResponse();
        response.Results["AttributeId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is CreateAttributeRequest car) captured = car;
            })
            .ReturnsAsync(response);

        await _service.CreateColumnAsync(request);

        captured.Should().NotBeNull("SDK request should have been captured");
        return captured!;
    }

    [Fact]
    public async Task String_CreatesStringAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.String);
        request.MaxLength = 250;
        request.Format = "Email";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<StringAttributeMetadata>().Subject;
        attr.MaxLength.Should().Be(250);
        attr.FormatName.Should().Be(StringFormatName.Email);
    }

    [Fact]
    public async Task String_DefaultMaxLength100()
    {
        var request = MakeRequest(SchemaColumnType.String);

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<StringAttributeMetadata>().Subject;
        attr.MaxLength.Should().Be(100);
    }

    [Fact]
    public async Task Memo_CreatesMemoAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Memo);
        request.MaxLength = 5000;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<MemoAttributeMetadata>().Subject;
        attr.MaxLength.Should().Be(5000);
    }

    [Fact]
    public async Task Integer_CreatesIntegerAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Integer);
        request.MinValue = 0;
        request.MaxValue = 1000;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<IntegerAttributeMetadata>().Subject;
        attr.MinValue.Should().Be(0);
        attr.MaxValue.Should().Be(1000);
    }

    [Fact]
    public async Task BigInt_CreatesBigIntAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.BigInt);

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        sdkRequest.Attribute.Should().BeOfType<BigIntAttributeMetadata>();
    }

    [Fact]
    public async Task Decimal_CreatesDecimalAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Decimal);
        request.MinValue = 0;
        request.MaxValue = 999.99;
        request.Precision = 2;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<DecimalAttributeMetadata>().Subject;
        attr.MinValue.Should().Be(0m);
        attr.MaxValue.Should().Be(999.99m);
        attr.Precision.Should().Be(2);
    }

    [Fact]
    public async Task Double_CreatesDoubleAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Double);
        request.MinValue = -100.5;
        request.MaxValue = 100.5;
        request.Precision = 3;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<DoubleAttributeMetadata>().Subject;
        attr.MinValue.Should().Be(-100.5);
        attr.MaxValue.Should().Be(100.5);
        attr.Precision.Should().Be(3);
    }

    [Fact]
    public async Task Money_CreatesMoneyAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Money);
        request.MinValue = 0;
        request.MaxValue = 1000000;
        request.Precision = 2;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<MoneyAttributeMetadata>().Subject;
        attr.MinValue.Should().Be(0);
        attr.MaxValue.Should().Be(1000000);
        attr.Precision.Should().Be(2);
    }

    [Fact]
    public async Task Boolean_CreatesBooleanAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Boolean);
        request.TrueLabel = "Active";
        request.FalseLabel = "Inactive";
        request.DefaultValue = 1;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<BooleanAttributeMetadata>().Subject;
        attr.OptionSet.Should().NotBeNull();
        attr.OptionSet!.TrueOption!.Label.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Active");
        attr.OptionSet.FalseOption!.Label.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Inactive");
        attr.DefaultValue.Should().BeTrue();
    }

    [Fact]
    public async Task DateTime_CreatesDateTimeAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.DateTime);
        request.DateTimeBehavior = "DateOnly";
        request.Format = "DateOnly";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<DateTimeAttributeMetadata>().Subject;
        attr.DateTimeBehavior.Should().Be(Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.DateOnly);
        attr.Format.Should().Be(DateTimeFormat.DateOnly);
    }

    [Fact]
    public async Task Choice_WithLocalOptions_CreatesPicklistAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Choice);
        request.Options = new[]
        {
            new OptionDefinition { Label = "Option A", Value = 100000 },
            new OptionDefinition { Label = "Option B", Value = 100001 }
        };

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<PicklistAttributeMetadata>().Subject;
        attr.OptionSet.Should().NotBeNull();
        attr.OptionSet!.Options.Should().HaveCount(2);
    }

    [Fact]
    public async Task Choice_WithGlobalOptionSet_CreatesPicklistWithGlobalRef()
    {
        var request = MakeRequest(SchemaColumnType.Choice);
        request.OptionSetName = "new_globalstatus";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<PicklistAttributeMetadata>().Subject;
        attr.OptionSet.Should().NotBeNull();
        attr.OptionSet!.IsGlobal.Should().BeTrue();
        attr.OptionSet.Name.Should().Be("new_globalstatus");
    }

    [Fact]
    public async Task Choices_CreatesMultiSelectPicklistAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Choices);
        request.Options = new[]
        {
            new OptionDefinition { Label = "Tag 1", Value = 100000 },
            new OptionDefinition { Label = "Tag 2", Value = 100001 }
        };

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<MultiSelectPicklistAttributeMetadata>().Subject;
        attr.OptionSet.Should().NotBeNull();
        attr.OptionSet!.Options.Should().HaveCount(2);
    }

    [Fact]
    public async Task Image_CreatesImageAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.Image);
        request.MaxSizeInKB = 10240;
        request.CanStoreFullImage = true;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<ImageAttributeMetadata>().Subject;
        attr.MaxSizeInKB.Should().Be(10240);
        attr.CanStoreFullImage.Should().BeTrue();
    }

    [Fact]
    public async Task File_CreatesFileAttributeMetadata()
    {
        var request = MakeRequest(SchemaColumnType.File);
        request.MaxSizeInKB = 65536;

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        var attr = sdkRequest.Attribute.Should().BeOfType<FileAttributeMetadata>().Subject;
        attr.MaxSizeInKB.Should().Be(65536);
    }

    [Fact]
    public async Task Lookup_ThrowsValidationException()
    {
        var request = MakeRequest(SchemaColumnType.Lookup);

        var act = () => _service.CreateColumnAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ValidationMessages.Any(m => m.Rule == MetadataErrorCodes.UseRelationshipForLookup));
    }

    [Fact]
    public async Task AllTypes_SetSchemaNameAndDisplayName()
    {
        var request = MakeRequest(SchemaColumnType.String);
        request.DisplayName = "My Custom Name";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        sdkRequest.Attribute.SchemaName.Should().Be("new_TestField");
        sdkRequest.Attribute.DisplayName.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("My Custom Name");
    }

    [Fact]
    public async Task AllTypes_SetRequiredLevel()
    {
        var request = MakeRequest(SchemaColumnType.String);
        request.RequiredLevel = "Required";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        sdkRequest.Attribute.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
    }

    [Fact]
    public async Task AllTypes_SetDescription()
    {
        var request = MakeRequest(SchemaColumnType.Integer);
        request.Description = "A test description";

        var sdkRequest = await CaptureCreateAttributeRequest(request);

        sdkRequest.Attribute.Description.Should().NotBeNull();
        sdkRequest.Attribute.Description!.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("A test description");
    }
}
