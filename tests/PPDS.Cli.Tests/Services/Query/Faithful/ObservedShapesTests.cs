using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The closed world, derived from the audit itself: the shapes the evaluator accepts (<see cref="ObservedShapes.All"/>) must
/// be exactly the shapes that the audit's 436 replayed cases and its OBSERVED raw-FetchXML lines send. An accepted shape that
/// no recorded case sends fails here, and so does a shape a case sends that the list lacks.
/// </summary>
[Trait("Category", "Unit")]
public class ObservedShapesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TheAcceptedShapesAreExactlyTheShapesTheAuditReplaySends()
    {
        var sent = await DeriveAsync();

        var acceptedButNotSent = ObservedShapes.All.Except(sent).Order(StringComparer.Ordinal).ToList();
        var sentButNotAccepted = sent.Except(ObservedShapes.All).Order(StringComparer.Ordinal).ToList();
        foreach (var shape in sent.Order(StringComparer.Ordinal))
            output.WriteLine($"        \"{shape}\",");
        Assert.True(acceptedButNotSent.Count == 0 && sentButNotAccepted.Count == 0,
            $"accepted but sent by no recorded case (refuse them): {string.Join("; ", acceptedButNotSent)}\n" +
            $"sent but not accepted: {string.Join("; ", sentButNotAccepted)}");
    }

    [Fact]
    public void AShapeOutsideTheSetIsRefusedByName()
    {
        var table = new FakeTable("t").Column("f", FakeColumnType.Double);
        table.Row(("f", 1.0));
        var error = Assert.Throws<NotSupportedException>(() => new FakeDataverse().Add(table)
            .RetrieveMultiple("<fetch><entity name=\"t\"><attribute name=\"f\" /><filter><condition attribute=\"f\" operator=\"eq\" value=\"1\" /></filter></entity></fetch>"));
        Assert.Contains("not observed: the shape 'condition eq Double'", error.Message, StringComparison.Ordinal);
    }

    private static async Task<HashSet<string>> DeriveAsync()
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        FetchXmlEvaluator.DerivingShapes.Value = sent;
        try
        {
            foreach (var c in AuditCaseReplay.Cases)
                await AuditCaseReplay.ReplayAsync(c);
            RunObservedLines();
        }
        finally
        {
            FetchXmlEvaluator.DerivingShapes.Value = null;
        }
        return sent;
    }

    /// <summary>Runs every OBSERVED test of <see cref="AuditObservedReplayTests"/> with its own data.</summary>
    private static void RunObservedLines()
    {
        var type = typeof(AuditObservedReplayTests);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<FactAttribute>() is null)
                continue;
            var data = method.GetCustomAttribute<MemberDataAttribute>() is { } member
                ? (IEnumerable<object[]>)type.GetMethod(member.MemberName, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!
                : [[]];
            foreach (var args in data)
                method.Invoke(Activator.CreateInstance(type), args);
        }
    }
}
