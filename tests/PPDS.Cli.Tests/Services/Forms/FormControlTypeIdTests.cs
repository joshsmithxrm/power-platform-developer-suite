using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public sealed class FormControlTypeIdTests
{
    [Fact]
    public void Fixture_PassesBundledSchema()
    {
        var errors = new List<string>();
        XDocument.Parse(ControlTypeFixture.Xml()).Validate(FormSchemaResources.GetSchemaSet(), (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetXml_RepeatedTypeIds_PreservesExactInput(bool sameAsInstance)
    {
        var xml = ControlTypeFixture.Xml();
        if (sameAsInstance) xml = xml.Replace("{22222222-2222-2222-2222-222222222222}", ControlTypeFixture.TypeId);
        var h = new ControlTypeHarness(xml);
        using var cts = new CancellationTokenSource();
        await h.Service.SetFormXmlAsync(new("account", ControlTypeHarness.FormId.ToString(), xml), ct: cts.Token);
        var update = Assert.Single(h.Writes);
        Assert.Equal(xml, update.GetAttributeValue<string>("formxml"));
        Assert.Equal(ControlTypeHarness.FormId, update.Id);
        Assert.Equal("systemform", update.LogicalName);
        Assert.Equal(new[] { "formxml" }, update.Attributes.Keys);
        Assert.All(h.Tokens, token => Assert.Equal(cts.Token, token));
        Assert.DoesNotContain(h.Requests, r => r is PublishXmlRequest or AddSolutionComponentRequest);
    }

    [Theory]
    [InlineData("tab-section", FormErrorCodes.DuplicateGuid)]
    [InlineData("cells", FormErrorCodes.DuplicateGuid)]
    [InlineData("labels", FormErrorCodes.DuplicateGuid)]
    [InlineData("id-label", FormErrorCodes.DuplicateGuid)]
    [InlineData("type-format", FormErrorCodes.InvalidFormXml)]
    [InlineData("missing-reference", FormErrorCodes.InvalidFormXml)]
    [InlineData("misplaced", FormErrorCodes.InvalidFormXml)]
    [InlineData("namespaced", FormErrorCodes.InvalidFormXml)]
    [InlineData("nested-parameter", FormErrorCodes.DuplicateGuid)]
    public async Task InvalidXml_DoesNotWrite(string mutation, string code)
    {
        var xml = ControlTypeFixture.InvalidXml(mutation);
        var h = new ControlTypeHarness(xml);
        var error = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.SetFormXmlAsync(
            new("account", ControlTypeHarness.FormId.ToString(), xml, "fixture_solution", true)));
        Assert.Equal(code, error.ErrorCode);
        h.AssertNoWrites();
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("ancestor-namespace")]
    [InlineData("nested-form")]
    [InlineData("wrong-parent")]
    [InlineData("labelid")]
    public void GuidUniqueness_DoesNotExemptLookalikes(string variant)
    {
        // Exercise the GUID check itself so XSD rejection cannot mask an overbroad exemption.
        var doc = XDocument.Parse(ControlTypeFixture.Xml());
        var types = doc.Descendants("customControl").Where(c => c.Attribute("id") != null).ToList();
        if (variant == "namespace") foreach (var type in types) type.Name = XName.Get("customControl", "urn:lookalike");
        if (variant == "ancestor-namespace") doc.Root!.Element("controlDescriptions")!.Name = XName.Get("controlDescriptions", "urn:lookalike");
        if (variant == "nested-form") doc = new XDocument(new XElement("wrapper", doc.Root));
        if (variant == "wrong-parent") foreach (var type in types) type.Parent!.Name = "other";
        if (variant == "labelid") foreach (var type in types) { type.Attribute("id")!.Remove(); type.SetAttributeValue("labelid", ControlTypeFixture.TypeId); }
        var error = Assert.Throws<PpdsValidationException>(() => FormXmlValidator.ValidateGuids(doc));
        Assert.Equal(FormErrorCodes.DuplicateGuid, error.ErrorCode);
    }

    [Fact]
    public async Task CancelledSet_DoesNotWrite()
    {
        var h = new ControlTypeHarness(ControlTypeFixture.Xml());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.SetFormXmlAsync(
            new("account", ControlTypeHarness.FormId.ToString(), h.Xml), ct: cts.Token));
        h.AssertNoWrites();
    }

    [Fact]
    public async Task ConfirmationDenied_DoesNotAcquireClient()
    {
        var h = new ControlTypeHarness(ControlTypeFixture.Xml());
        h.Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException("Environment.ReadOnly", "Fixture refuses writes."));
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.SetFormXmlAsync(
            new("account", ControlTypeHarness.FormId.ToString(), h.Xml)));
        Assert.Equal("Environment.ReadOnly", error.ErrorCode);
        h.Pool.VerifyNoOtherCalls();
        h.AssertNoWrites();
    }
}

internal static class ControlTypeFixture
{
    internal const string TypeId = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";
    internal static string Xml()
    {
        // Synthetic full-schema fixture, not a customer export. The type/reference pattern follows
        // Microsoft's work-order map example; uniqueid/forControl identify the distinct instances.
        var doc = XDocument.Parse(TimelineFormFixture.Xml);
        var first = doc.Descendants("control").First();
        first.SetAttributeValue("uniqueid", "{11111111-1111-1111-1111-111111111111}");
        doc.Descendants("rows").Single().Add(new XElement("row",
            new XElement("cell", new XAttribute("id", "{88888888-8888-8888-8888-888888888888}"),
                new XElement("control", new XAttribute("id", "telephone1"), new XAttribute("datafieldname", "telephone1"),
                    new XAttribute("classid", TypeId), new XAttribute("uniqueid", "{99999999-9999-9999-9999-999999999999}")))));
        doc.Root!.Add(new XElement("controlDescriptions",
            new XElement("controlDescription", new XAttribute("forControl", "{11111111-1111-1111-1111-111111111111}"),
                new XElement("customControl", new XAttribute("id", TypeId),
                    new XElement("parameters", new XElement("datafieldname", "name"))),
                new XElement("customControl", new XAttribute("name", "Fixture.TextControl"), new XAttribute("formFactor", 0),
                    new XElement("parameters", new XElement("value", "name")))),
            new XElement("controlDescription", new XAttribute("forControl", "{99999999-9999-9999-9999-999999999999}"),
                new XElement("customControl", new XAttribute("id", TypeId.Trim('{', '}').ToUpperInvariant()),
                    new XElement("parameters", new XElement("datafieldname", "telephone1"))))));
        return "<?xml version=\"1.0\"?>\r\n<!-- preserve formatting and references -->\r\n" + doc + "\r\n";
    }

    internal static string InvalidXml(string mutation)
    {
        var doc = XDocument.Parse(Xml());
        var tab = doc.Descendants("tab").Single();
        var section = doc.Descendants("section").Single();
        var types = doc.Descendants("customControl").Where(c => c.Attribute("id") != null).ToList();
        switch (mutation)
        {
            case "tab-section": section.SetAttributeValue("id", ((string)tab.Attribute("id")!).Trim('{', '}').ToLowerInvariant()); break;
            case "cells": doc.Descendants("cell").Last().SetAttributeValue("id", (string?)doc.Descendants("cell").First().Attribute("id")); break;
            case "labels": tab.SetAttributeValue("labelid", "{abcdefab-abcd-abcd-abcd-abcdefabcdef}"); section.SetAttributeValue("labelid", "ABCDEFAB-ABCD-ABCD-ABCD-ABCDEFABCDEF"); break;
            case "id-label": section.SetAttributeValue("labelid", (string?)tab.Attribute("id")); break;
            case "type-format": types[0].SetAttributeValue("id", "not-a-guid"); break;
            case "missing-reference": types[0].Parent!.Attribute("forControl")!.Remove(); break;
            case "misplaced": types[0].Remove(); doc.Root!.Add(types[0]); break;
            case "namespaced": types[0].Name = XName.Get("customControl", "urn:lookalike"); break;
            case "nested-parameter":
                types[0].Element("parameters")!.Add(new XElement("customControl", new XAttribute("id", "{abcdefab-abcd-abcd-abcd-abcdefabcdef}")),
                    new XElement("customControl", new XAttribute("id", "ABCDEFAB-ABCD-ABCD-ABCD-ABCDEFABCDEF")));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        return doc.ToString();
    }
}

internal sealed class ControlTypeHarness
{
    internal static readonly Guid FormId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    internal string Xml { get; }
    internal List<Entity> Writes { get; } = [];
    internal List<OrganizationRequest> Requests { get; } = [];
    internal List<QueryExpression> Queries { get; } = [];
    internal List<CancellationToken> Tokens { get; } = [];
    internal Mock<IDataverseConnectionPool> Pool { get; } = new();
    internal Mock<IEnvironmentConfirmationService> Confirmation { get; } = new();
    internal FormService Service { get; }

    internal ControlTypeHarness(string xml)
    {
        Xml = xml;
        var state = new Entity("systemform", FormId) { ["formid"] = FormId, ["name"] = "Fixture", ["type"] = new OptionSetValue(2),
            ["formxml"] = xml, ["ismanaged"] = false, ["iscustomizable"] = new BooleanManagedProperty(true) };
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken ct) =>
            {
                Tokens.Add(ct); ct.ThrowIfCancellationRequested(); Requests.Add(request);
                if (request is RetrieveUnpublishedMultipleRequest retrieve)
                {
                    Queries.Add((QueryExpression)retrieve.Query);
                    return new RetrieveUnpublishedMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection([state]) } };
                }
                return new OrganizationResponse();
            });
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase query, CancellationToken ct) => { Tokens.Add(ct); ct.ThrowIfCancellationRequested(); Queries.Add((QueryExpression)query); return new EntityCollection([state]); });
        client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((entity, ct) => { Tokens.Add(ct); ct.ThrowIfCancellationRequested(); Writes.Add(entity); }).Returns(Task.CompletedTask);
        Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(i => { var ct = (CancellationToken)i.Arguments[2]; Tokens.Add(ct); ct.ThrowIfCancellationRequested(); }))
            .ReturnsAsync(client.Object);
        Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Service = new FormService(Pool.Object, Mock.Of<IMetadataQueryService>(), NullLogger<FormService>.Instance, Confirmation.Object);
    }

    internal ServiceProvider Provider() => new ServiceCollection().AddSingleton<IFormService>(Service).AddSingleton(Confirmation.Object)
        .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "offline-fixture", Username = "fixture@example.test" },
            EnvironmentUrl = "https://forms.example.test" }).BuildServiceProvider();

    internal void AssertNoWrites()
    {
        Assert.Empty(Writes);
        Assert.DoesNotContain(Requests, r => r is PublishXmlRequest or AddSolutionComponentRequest);
    }
}
