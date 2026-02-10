using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDbParameterCollectionTests
{
    // ────────────────────────────────────────────
    //  Add
    // ────────────────────────────────────────────

    [Fact]
    public void Add_PpdsDbParameter_ReturnsIndex()
    {
        var collection = new PpdsDbParameterCollection();
        var param = new PpdsDbParameter("@name", "Contoso");

        var index = collection.Add((object)param);

        index.Should().Be(0);
        collection.Count.Should().Be(1);
    }

    [Fact]
    public void Add_StronglyTyped_ReturnsParameter()
    {
        var collection = new PpdsDbParameterCollection();
        var param = new PpdsDbParameter("@name", "Contoso");

        var result = collection.Add(param);

        result.Should().BeSameAs(param);
    }

    [Fact]
    public void AddWithValue_CreatesAndAddsParameter()
    {
        var collection = new PpdsDbParameterCollection();

        var param = collection.AddWithValue("@name", "Contoso");

        param.ParameterName.Should().Be("@name");
        param.Value.Should().Be("Contoso");
        collection.Count.Should().Be(1);
    }

    [Fact]
    public void Add_NonPpdsDbParameter_ThrowsArgumentException()
    {
        var collection = new PpdsDbParameterCollection();

        var act = () => collection.Add("not a parameter");

        act.Should().Throw<ArgumentException>();
    }

    // ────────────────────────────────────────────
    //  Contains
    // ────────────────────────────────────────────

    [Fact]
    public void Contains_ByObject_ReturnsTrueForExisting()
    {
        var collection = new PpdsDbParameterCollection();
        var param = new PpdsDbParameter("@name", "Contoso");
        collection.Add(param);

        collection.Contains((object)param).Should().BeTrue();
    }

    [Fact]
    public void Contains_ByName_ReturnsTrueForExisting()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@name", "Contoso");

        collection.Contains("@name").Should().BeTrue();
    }

    [Fact]
    public void Contains_ByName_CaseInsensitive()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@Name", "Contoso");

        collection.Contains("@name").Should().BeTrue();
    }

    [Fact]
    public void Contains_ByName_ReturnsFalseForMissing()
    {
        var collection = new PpdsDbParameterCollection();

        collection.Contains("@name").Should().BeFalse();
    }

    // ────────────────────────────────────────────
    //  IndexOf
    // ────────────────────────────────────────────

    [Fact]
    public void IndexOf_ByName_ReturnsCorrectIndex()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@first", 1);
        collection.AddWithValue("@second", 2);

        collection.IndexOf("@second").Should().Be(1);
    }

    [Fact]
    public void IndexOf_ByName_NotFound_ReturnsNegativeOne()
    {
        var collection = new PpdsDbParameterCollection();

        collection.IndexOf("@missing").Should().Be(-1);
    }

    [Fact]
    public void IndexOf_ByObject_ReturnsCorrectIndex()
    {
        var collection = new PpdsDbParameterCollection();
        var param1 = collection.AddWithValue("@first", 1);
        var param2 = collection.AddWithValue("@second", 2);

        collection.IndexOf((object)param2).Should().Be(1);
    }

    // ────────────────────────────────────────────
    //  Remove
    // ────────────────────────────────────────────

    [Fact]
    public void RemoveAt_ByIndex_RemovesParameter()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@first", 1);
        collection.AddWithValue("@second", 2);

        collection.RemoveAt(0);

        collection.Count.Should().Be(1);
        collection.Contains("@second").Should().BeTrue();
    }

    [Fact]
    public void RemoveAt_ByName_RemovesParameter()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@first", 1);
        collection.AddWithValue("@second", 2);

        collection.RemoveAt("@first");

        collection.Count.Should().Be(1);
        collection.Contains("@first").Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@first", 1);
        collection.AddWithValue("@second", 2);

        collection.Clear();

        collection.Count.Should().Be(0);
    }

    // ────────────────────────────────────────────
    //  Insert
    // ────────────────────────────────────────────

    [Fact]
    public void Insert_AtIndex_ShiftsExisting()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@first", 1);
        collection.AddWithValue("@third", 3);

        var middle = new PpdsDbParameter("@second", 2);
        collection.Insert(1, middle);

        collection.Count.Should().Be(3);
        collection.IndexOf("@second").Should().Be(1);
    }

    // ────────────────────────────────────────────
    //  Enumerate
    // ────────────────────────────────────────────

    [Fact]
    public void GetEnumerator_IteratesAllParameters()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@a", 1);
        collection.AddWithValue("@b", 2);
        collection.AddWithValue("@c", 3);

        var names = new List<string>();
        foreach (PpdsDbParameter param in collection)
        {
            names.Add(param.ParameterName);
        }

        names.Should().BeEquivalentTo("@a", "@b", "@c");
    }

    // ────────────────────────────────────────────
    //  Get / Set by name
    // ────────────────────────────────────────────

    [Fact]
    public void Indexer_ByName_GetsParameter()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@name", "Contoso");

        // Access via DbParameterCollection indexer (string)
        var param = (PpdsDbParameter)collection["@name"];

        param.Value.Should().Be("Contoso");
    }

    [Fact]
    public void Indexer_ByIndex_GetsParameter()
    {
        var collection = new PpdsDbParameterCollection();
        collection.AddWithValue("@name", "Contoso");

        var param = (PpdsDbParameter)collection[0];

        param.ParameterName.Should().Be("@name");
    }
}
