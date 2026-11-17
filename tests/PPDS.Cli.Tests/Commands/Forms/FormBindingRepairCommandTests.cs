using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

/// <summary>Offline command coverage of forms repair-bindings (#1527), through the real FormService and a mocked SDK.</summary>
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class FormBindingRepairCommandTests
{
    [Fact]
    [Trait("AC", "AC-78")]
    public async Task Json_ListsRepairedAndUnchangedControls_WithLocations()
    {
        var (h, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: form =>
        {
            FormBindingRepairTests.MisCasedIdAndUnknownBinding(form);
            form.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1")
                .SetAttributeValue("datafieldname", "Telephone1");
        }), ["--output-format", "Json"]);
        Assert.True(exit == ExitCodes.Success, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(new[] { "entity", "form", "formId", "dryRun", "message", "repairedCount", "unchangedCount", "formXmlChanged",
            "solutionAssociationRequested", "publicationRequested", "repaired", "unchanged" }, data.EnumerateObject().Select(p => p.Name));
        Assert.False(data.GetProperty("dryRun").GetBoolean());
        Assert.True(data.GetProperty("formXmlChanged").GetBoolean());
        Assert.Equal(2, data.GetProperty("repairedCount").GetInt32());
        Assert.Equal(2, data.GetProperty("unchangedCount").GetInt32());

        var repaired = data.GetProperty("repaired").EnumerateArray().ToList();
        Assert.Equal(new[] { "controlId", "oldBinding", "newBinding", "location", "sectionId" }, repaired[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(new (string?, string?, string?, string?)[] { ("Name", "Name", "name", "My Tab / General"), ("telephone1", "Telephone1", "telephone1", "My Tab / Other") },
            repaired.Select(r => (r.GetProperty("controlId").GetString(), r.GetProperty("oldBinding").GetString(),
                r.GetProperty("newBinding").GetString(), r.GetProperty("location").GetString())));
        Assert.Equal("{55555555-5555-5555-5555-555555555555}", repaired[1].GetProperty("sectionId").GetString());

        var unchanged = data.GetProperty("unchanged").EnumerateArray().ToList();
        Assert.Equal(new[] { "controlId", "binding", "reason", "location", "sectionId", "canonicalName" }, unchanged[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(new (string?, string?, string?)[] { ("Name", "control-id-case-differs", "name"), ("new_gone", "no-matching-column", null) },
            unchanged.Select(u => (u.GetProperty("controlId").GetString(), u.GetProperty("reason").GetString(),
                u.TryGetProperty("canonicalName", out var canonical) ? canonical.GetString() : null)));
        // The JSON writer omits null values: no canonical name when no column matches.
        Assert.False(unchanged[1].TryGetProperty("canonicalName", out _));
        Assert.Single(h!.Writes);
    }

    [Fact]
    [Trait("AC", "AC-78")]
    public async Task Text_WritesStatusToStderrOnly()
    {
        var (_, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedIdAndUnknownBinding), []);
        Assert.True(exit == ExitCodes.Success, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Repaired 1 binding(s) on form 'Fixture'.", stderr);
        Assert.Contains("binding 'Name' -> 'name'", stderr);
        Assert.Contains("is bound to 'New_Gone', which matches no column on 'account'", stderr);
        Assert.Contains("control ids are not rewritten", stderr);
        Assert.Contains("saved unpublished", stderr);
    }

    [Fact]
    [Trait("AC", "AC-75")]
    public async Task NothingToRepair_ExitsSuccessWithoutUpdate()
    {
        var (h, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct), ["--output-format", "Json"]);
        Assert.True(exit == ExitCodes.Success, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("repairedCount").GetInt32());
        Assert.False(data.GetProperty("formXmlChanged").GetBoolean());
        Assert.Equal(0, data.GetProperty("repaired").GetArrayLength());
        Assert.Empty(h!.Writes);
    }

    [Fact]
    [Trait("AC", "AC-76")]
    public async Task DryRun_NeedsNoAcknowledgement_AndWritesNothing()
    {
        var (h, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedEverywhere)
            { DenyConfirmation = true }, ["--dry-run", "--output-format", "Json"], confirm: false);
        Assert.True(exit == ExitCodes.Success, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.False(data.GetProperty("formXmlChanged").GetBoolean());
        Assert.Equal(4, data.GetProperty("repairedCount").GetInt32());
        Assert.Contains("Nothing was written", data.GetProperty("message").GetString());
        Assert.Empty(h!.Writes);
        Assert.DoesNotContain("confirmation", h.Journal);
    }

    [Theory]
    [Trait("AC", "AC-76")]
    [InlineData("--publish")]
    [InlineData("--solution", "fixture_solution")]
    public async Task DryRun_WithExplicitSideEffect_IsRejectedBeforeAnyCall(params string[] extra)
    {
        var providerCreated = false;
        var (_, exit, _, stderr) = await Run(ct => { providerCreated = true; return new ContributorFormHarness(ct); }, ["--dry-run", .. extra]);
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Contains("--dry-run cannot be combined", stderr);
        // Rejected before connecting: the provider (and so any confirmation, read or write) is never created.
        Assert.False(providerCreated);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task DeniedConfirmation_FailsWithoutWriting()
    {
        var (h, exit, _, _) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedEverywhere)
            { DenyConfirmation = true }, ["--output-format", "Json"]);
        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Empty(h!.Writes);
        Assert.Equal(0, h.Acquisitions);
    }

    [Fact]
    [Trait("AC", "AC-78")]
    public void Help_DescribesBindingOnlyRepair_AndOptions()
    {
        var command = RepairBindingsCommand.Create();
        Assert.Equal("repair-bindings", command.Name);
        Assert.Contains("Only the binding changes", command.Description);
        Assert.Contains("left unchanged", command.Description);
        foreach (var option in new[] { "--entity", "--form", "--dry-run", "--publish", "--solution", "--confirm-env", "--profile", "--environment" })
            Assert.Contains(command.Options, o => o.Name == option);
        Assert.Contains("table", command.Options.Single(o => o.Name == "--publish").Description);
    }

    [Fact]
    [Trait("AC", "AC-79")]
    public async Task CaseVariantWarning_PointsToRepairBindings()
    {
        var command = AddFieldCommand.Create((_, _, _, ct) => Task.FromResult(new ContributorFormHarness(ct, transform: doc =>
            doc.Descendants("control").First(c => (string?)c.Attribute("datafieldname") == "name")
                .SetAttributeValue("datafieldname", "Name")).CreateTextProvider()));
        var (exit, stdout, stderr) = await Invoke(command,
            ["--entity", "account", "--form", "Fixture", "--section", "General", "--field", "name", "--confirm-env", "https://forms.example.test"]);
        Assert.True(exit == ExitCodes.Success, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("bound to 'Name'", stderr);
        Assert.Contains("ppds forms repair-bindings", stderr);
        Assert.Contains("--dry-run", stderr);
        Assert.DoesNotContain("forms get --raw", stderr);
    }

    private static async Task<(ContributorFormHarness? Harness, int Exit, string Out, string Error)> Run(
        Func<CancellationToken, ContributorFormHarness> create, string[] args, bool confirm = true)
    {
        // The harness's mocks expect the command's own cancellation token, so it is built inside the provider factory.
        ContributorFormHarness? harness = null;
        var command = RepairBindingsCommand.Create((_, _, _, ct) => Task.FromResult((harness = create(ct)).CreateTextProvider()));
        var all = new List<string> { "--entity", "account", "--form", "Fixture" };
        all.AddRange(args);
        if (confirm) all.AddRange(["--confirm-env", "https://forms.example.test"]);
        var (exit, stdout, stderr) = await Invoke(command, all.ToArray());
        return (harness, exit, stdout, stderr);
    }

    private static async Task<(int Exit, string Out, string Error)> Invoke(Command command, string[] args)
    {
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse(args);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
