using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// Columns, cultures and a metadata-serving pool shared by the value conversion tests of #1916 and #1917.
/// </summary>
internal static class ColumnValueTestSupport
{
    /// <summary>
    /// The invariant culture, two cultures whose decimal separator is a comma (de-DE groups with a point, fr-FR with a
    /// space), and one whose native digits are not ASCII (ar-SA).
    /// </summary>
    public static readonly string[] Cultures = ["", "de-DE", "fr-FR", "ar-SA"];

    /// <summary>Runs <paramref name="body"/> once under each of <see cref="Cultures"/> and returns each result.</summary>
    public static List<T> InEveryCulture<T>(Func<T> body)
    {
        var results = new List<T>();
        foreach (var name in Cultures)
        {
            var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                results.Add(body());
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = ui;
            }
        }

        return results;
    }

    /// <summary>Runs <paramref name="body"/> once under each of <see cref="Cultures"/>, awaiting each run.</summary>
    public static async Task<List<T>> InEveryCultureAsync<T>(Func<Task<T>> body)
    {
        var results = new List<T>();
        foreach (var name in Cultures)
        {
            var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                results.Add(await body());
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = ui;
            }
        }

        return results;
    }

    /// <summary>A culture-independent description of a value as it would be sent.</summary>
    public static string Describe(object? value) => value switch
    {
        null => "null",
        string s => $"text:{s}",
        int i => $"int:{i.ToString(CultureInfo.InvariantCulture)}",
        long l => $"long:{l.ToString(CultureInfo.InvariantCulture)}",
        decimal d => $"decimal:{d.ToString(CultureInfo.InvariantCulture)}",
        double x => $"double:{(double.IsNegative(x) ? "-" : "")}{Math.Abs(x).ToString("R", CultureInfo.InvariantCulture)}",
        bool b => $"bool:{b}",
        Guid g => $"guid:{g:D}",
        Money m => $"money:{m.Value.ToString(CultureInfo.InvariantCulture)}",
        OptionSetValue o => $"option:{o.Value.ToString(CultureInfo.InvariantCulture)}",
        EntityReference r => $"ref:{r.LogicalName}:{r.Id:D}",
        DateTime t => $"date:{t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}:{t.Kind}",
        _ => $"{value.GetType().Name}:{value}"
    };

    public static StringAttributeMetadata Text(string name) => new() { LogicalName = name };

    public static IntegerAttributeMetadata WholeNumber(string name) => new() { LogicalName = name };

    public static BigIntAttributeMetadata BigInt(string name) => new() { LogicalName = name };

    public static DecimalAttributeMetadata Decimal(string name, int? precision) => new() { LogicalName = name, Precision = precision };

    public static DoubleAttributeMetadata FloatingPoint(string name, int? precision) => new() { LogicalName = name, Precision = precision };

    /// <summary>A currency column; <paramref name="precisionSource"/> 0 is the column's own precision, 1 the organization's
    /// pricing precision, 2 the record's currency.</summary>
    public static MoneyAttributeMetadata Currency(string name, int? precision, int? precisionSource = 0) =>
        new() { LogicalName = name, Precision = precision, PrecisionSource = precisionSource };

    public static BooleanAttributeMetadata YesNo(string name) => new() { LogicalName = name };

    public static UniqueIdentifierAttributeMetadata Identifier(string name) => new() { LogicalName = name };

    public static PicklistAttributeMetadata Choice(string name, params int[] values)
    {
        var options = new OptionSetMetadata();
        foreach (var v in values)
        {
            options.Options.Add(new OptionMetadata(new Label(), v));
        }

        return new PicklistAttributeMetadata { LogicalName = name, OptionSet = options };
    }

    public static LookupAttributeMetadata Lookup(string name, params string[] targets) =>
        new() { LogicalName = name, Targets = targets };

    public static DateTimeAttributeMetadata Date(string name, DateTimeBehavior? behavior, DateTimeFormat format) =>
        new() { LogicalName = name, DateTimeBehavior = behavior, Format = format };

    public static MultiSelectPicklistAttributeMetadata MultiChoice(string name) => new() { LogicalName = name };

    /// <summary>
    /// A pool whose clients answer <see cref="RetrieveEntityRequest"/> for <paramref name="entity"/> with these columns,
    /// and fail any other request.
    /// </summary>
    public static Mock<IDataverseConnectionPool> PoolWithColumns(string entity, params AttributeMetadata[] columns)
    {
        var metadata = new EntityMetadata { LogicalName = entity };
        typeof(EntityMetadata).GetProperty("PrimaryIdAttribute")!.SetValue(metadata, entity + "id");
        typeof(EntityMetadata).GetProperty("Attributes")!.SetValue(metadata, columns);

        var client = new Mock<IPooledClient>();
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken _) =>
            {
                if (request is RetrieveEntityRequest { } r && r.LogicalName == entity)
                {
                    var response = new RetrieveEntityResponse();
                    response.Results["EntityMetadata"] = metadata;
                    return response;
                }

                throw new InvalidOperationException($"Unexpected request {request.RequestName}.");
            });

        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return pool;
    }
}
