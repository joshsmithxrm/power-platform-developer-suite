using FluentAssertions;
using PPDS.DocsGen.Libs;
using Xunit;

namespace PPDS.DocsGen.Libs.Tests;

#pragma warning disable CS1591 // Fixture types; docs are synthesized in the test body.

public interface IInheritdocTestThing
{
    void DoWork();
    string Label { get; }
}

public class InheritdocTestThingImpl : IInheritdocTestThing
{
    public void DoWork() { }
    public string Label => "x";
}

public abstract class InheritdocBaseShape
{
    public virtual double Area() => 0;
}

public sealed class InheritdocDerivedShape : InheritdocBaseShape
{
    public override double Area() => 1;
}

#pragma warning restore CS1591

/// <summary>
/// Covers <c>&lt;inheritdoc /&gt;</c> resolution in <see cref="XmlDocIndex"/> —
/// the primary mechanism by which the credential providers in PPDS.Auth
/// surface documentation without duplicating their base-interface summaries.
/// </summary>
public sealed class XmlDocIndexInheritdocTests
{
    private static string WriteTempXml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "xmldoc-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ResolvesInheritdocFromInterface_Method()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <assembly><name>PPDS.DocsGen.Libs.Tests</name></assembly>
              <members>
                <member name="M:{{{typeof(IInheritdocTestThing).FullName}}}.DoWork">
                  <summary>Interface-level summary for DoWork.</summary>
                </member>
                <member name="M:{{{typeof(InheritdocTestThingImpl).FullName}}}.DoWork">
                  <inheritdoc />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocTestThingImpl).GetMethod(nameof(InheritdocTestThingImpl.DoWork))!);

            doc.Should().NotBeNull();
            doc!.Summary.Should().Be("Interface-level summary for DoWork.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvesInheritdocFromInterface_Property()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="P:{{{typeof(IInheritdocTestThing).FullName}}}.Label">
                  <summary>Interface-level summary for Label.</summary>
                </member>
                <member name="P:{{{typeof(InheritdocTestThingImpl).FullName}}}.Label">
                  <inheritdoc />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocTestThingImpl).GetProperty(nameof(InheritdocTestThingImpl.Label))!);

            doc.Should().NotBeNull();
            doc!.Summary.Should().Be("Interface-level summary for Label.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvesInheritdocFromBaseClass_Method()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="M:{{{typeof(InheritdocBaseShape).FullName}}}.Area">
                  <summary>Base-class summary for Area.</summary>
                </member>
                <member name="M:{{{typeof(InheritdocDerivedShape).FullName}}}.Area">
                  <inheritdoc />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocDerivedShape).GetMethod(nameof(InheritdocDerivedShape.Area))!);

            doc.Should().NotBeNull();
            doc!.Summary.Should().Be("Base-class summary for Area.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvesInheritdocWithExplicitCref()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:Some.Target.Type">
                  <summary>Target-of-cref summary.</summary>
                </member>
                <member name="M:{{{typeof(InheritdocTestThingImpl).FullName}}}.DoWork">
                  <inheritdoc cref="T:Some.Target.Type" />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocTestThingImpl).GetMethod(nameof(InheritdocTestThingImpl.DoWork))!);

            doc.Should().NotBeNull();
            doc!.Summary.Should().Be("Target-of-cref summary.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnresolvableInheritdoc_ReturnsOriginalInheritdocMarker()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="M:{{{typeof(InheritdocTestThingImpl).FullName}}}.DoWork">
                  <inheritdoc />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocTestThingImpl).GetMethod(nameof(InheritdocTestThingImpl.DoWork))!);

            doc.Should().NotBeNull();
            doc!.IsInheritDoc.Should().BeTrue(because: "no usable ancestor entry was found");
            doc.Summary.Should().BeNullOrEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
