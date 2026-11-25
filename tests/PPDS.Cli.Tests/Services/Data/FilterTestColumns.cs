using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>Column metadata for the filter tests: an account-like table with one column of each kind.</summary>
internal static class FilterTestColumns
{
    private static AttributeMetadataDto Column(string name, string type, Action<AttributeMetadataDtoBuilder>? set = null)
    {
        var builder = new AttributeMetadataDtoBuilder(name, type);
        set?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>The columns of the test table "account".</summary>
    public static IReadOnlyList<AttributeMetadataDto> Account() => new[]
    {
        Column("accountid", "Uniqueidentifier", b => b.PrimaryId = true),
        Column("name", "String", b => b.PrimaryName = true),
        Column("accountnumber", "String"),
        Column("code", "String"),
        Column("description", "Memo"),
        Column("numberofemployees", "Integer"),
        Column("price", "Decimal", b => b.Precision = 10),
        Column("revenue", "Money", b => b.Precision = 2),
        Column("ratio", "Double", b => b.Precision = 5),
        Column("createdon", "DateTime", b => { b.DateTimeBehavior = "UserLocal"; b.Format = "DateAndTime"; }),
        Column("birthdate", "DateTime", b => { b.DateTimeBehavior = "DateOnly"; b.Format = "DateOnly"; }),
        Column("lastseen", "DateTime", b => { b.DateTimeBehavior = "TimeZoneIndependent"; b.Format = "DateAndTime"; }),
        Column("shownasdate", "DateTime", b => { b.DateTimeBehavior = "UserLocal"; b.Format = "DateOnly"; }),
        Column("primarycontactid", "Lookup", b => b.Targets = new List<string> { "contact" }),
        Column("ownerid", "Owner", b => b.Targets = new List<string> { "systemuser", "team" }),
        Column("customerid", "Customer", b => b.Targets = new List<string> { "account", "contact" }),
        Column("statecode", "State"),
        Column("industrycode", "Picklist"),
        Column("donotemail", "Boolean"),
        Column("versionnumber", "BigInt"),
        Column("processid", "Uniqueidentifier"),
        Column("primarycontactidname", "String", b => b.AttributeOf = "primarycontactid"),
    };

    /// <summary>A metadata provider that answers <paramref name="columns"/> for "account" (or every table).</summary>
    public static ICachedMetadataProvider Provider(IReadOnlyList<AttributeMetadataDto>? columns = null)
    {
        var provider = new Mock<ICachedMetadataProvider>();
        provider.Setup(p => p.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(columns ?? Account());
        return provider.Object;
    }

    internal sealed class AttributeMetadataDtoBuilder
    {
        private readonly string _name;
        private readonly string _type;

        public AttributeMetadataDtoBuilder(string name, string type)
        {
            _name = name;
            _type = type;
        }

        public bool PrimaryId { get; set; }
        public bool PrimaryName { get; set; }
        public int? Precision { get; set; }
        public string? DateTimeBehavior { get; set; }
        public string? Format { get; set; }
        public List<string>? Targets { get; set; }
        public string? AttributeOf { get; set; }

        public AttributeMetadataDto Build() => new()
        {
            LogicalName = _name,
            DisplayName = _name,
            SchemaName = _name,
            AttributeType = _type,
            IsPrimaryId = PrimaryId,
            IsPrimaryName = PrimaryName,
            Precision = Precision,
            DateTimeBehavior = DateTimeBehavior,
            Format = Format,
            Targets = Targets,
            AttributeOf = AttributeOf,
        };
    }
}
