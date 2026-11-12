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

public interface IInheritdocIndexedValues
{
    object this[int ordinal] { get; }
    object this[string name] { get; }
}

public sealed class InheritdocIndexedValues : IInheritdocIndexedValues
{
    public object this[int ordinal] => ordinal;
    public object this[string name] => name;
    public object this[Guid id] => id;
}

#pragma warning restore CS1591

/// <summary>
/// Covers <c>&lt;inheritdoc /&gt;</c> resolution in <see cref="XmlDocIndex"/> —
/// the primary mechanism by which the credential providers in PPDS.Auth
/// surface documentation without duplicating their base-interface summaries.
/// </summary>
public sealed class XmlDocIndexInheritdocTests
{
    [Theory]
    [InlineData(typeof(int), "Ordinal lookup.")]
    [InlineData(typeof(string), "Named lookup.")]
    public void ResolvesOverloadedIndexerByParameterSignature(Type parameter, string expected)
    {
        var path = WriteTempXml($$$"""
            <doc><members>
              <member name="P:{{{typeof(IInheritdocIndexedValues).FullName}}}.Item(System.Int32)"><summary>Ordinal lookup.</summary></member>
              <member name="P:{{{typeof(IInheritdocIndexedValues).FullName}}}.Item(System.String)"><summary>Named lookup.</summary></member>
              <member name="P:{{{typeof(InheritdocIndexedValues).FullName}}}.Item({{{parameter.FullName}}})"><inheritdoc /></member>
            </members></doc>
            """);
        try
        {
            var member = typeof(InheritdocIndexedValues).GetProperty("Item", [parameter])!;
            XmlDocIndex.Load(path).ForMember(member)!.Summary.Should().Be(expected);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnmatchedIndexerDoesNotInheritAnotherOverloadsSummary()
    {
        var path = WriteTempXml($$$"""
            <doc><members>
              <member name="P:{{{typeof(IInheritdocIndexedValues).FullName}}}.Item(System.Int32)"><summary>Ordinal lookup.</summary></member>
              <member name="P:{{{typeof(IInheritdocIndexedValues).FullName}}}.Item(System.String)"><summary>Named lookup.</summary></member>
              <member name="P:{{{typeof(InheritdocIndexedValues).FullName}}}.Item(System.Guid)"><inheritdoc /></member>
            </members></doc>
            """);
        try
        {
            var member = typeof(InheritdocIndexedValues).GetProperty("Item", [typeof(Guid)])!;
            var doc = XmlDocIndex.Load(path).ForMember(member);
            doc.Should().NotBeNull();
            doc!.IsInheritDoc.Should().BeTrue();
            doc.Summary.Should().BeNullOrEmpty();
            doc.UnresolvedInheritTarget.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

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
    public void UnresolvableInheritdoc_SurfacesFallbackPointerFromInheritanceChain()
    {
        // No ancestor entry is documented, so the implicit resolver walks the
        // inheritance chain and reports the first candidate it finds (the
        // interface member) as the fallback pointer.
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
            doc!.IsInheritDoc.Should().BeTrue();
            doc.Summary.Should().BeNullOrEmpty();
            doc.UnresolvedInheritTarget
                .Should().Be($"{typeof(IInheritdocTestThing).FullName}.DoWork",
                    because: "the first unresolved candidate in the chain should surface as the fallback pointer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnresolvableInheritdoc_WithExplicitCref_SurfacesCrefAsPointer()
    {
        // cref points at a member that is not present in the XML map; the
        // explicit cref wins over any implicit chain candidate for the fallback
        // pointer, because that is what the author asked us to inherit from.
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="M:{{{typeof(InheritdocTestThingImpl).FullName}}}.DoWork">
                  <inheritdoc cref="M:Totally.Missing.Base.DoWork" />
                </member>
              </members>
            </doc>
            """);

        try
        {
            var index = XmlDocIndex.Load(path);
            var doc = index.ForMember(typeof(InheritdocTestThingImpl).GetMethod(nameof(InheritdocTestThingImpl.DoWork))!);

            doc.Should().NotBeNull();
            doc!.IsInheritDoc.Should().BeTrue();
            doc.Summary.Should().BeNullOrEmpty();
            doc.UnresolvedInheritTarget.Should().Be("Totally.Missing.Base.DoWork");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvableInheritdoc_LeavesUnresolvedInheritTargetNull()
    {
        var path = WriteTempXml($$$"""
            <?xml version="1.0"?>
            <doc>
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
            doc.UnresolvedInheritTarget
                .Should().BeNull(because: "successful resolution must not set the fallback pointer");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
