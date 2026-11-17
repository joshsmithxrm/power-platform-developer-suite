using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

/// <summary>Offline command coverage of forms repair-bindings (#1527), through the real FormService and a mocked SDK.</summary>
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class FormBindingRepairCommandTests
{
    private const string Target = "https://forms.example.test";

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
            "readBackVerified", "solutionAssociationRequested", "publicationRequested", "repaired", "unchanged", "readBackRepairable" },
            data.EnumerateObject().Select(p => p.Name));
        Assert.False(data.GetProperty("dryRun").GetBoolean());
        Assert.True(data.GetProperty("formXmlChanged").GetBoolean());
        Assert.True(data.GetProperty("readBackVerified").GetBoolean());
        Assert.Equal(2, data.GetProperty("repairedCount").GetInt32());
        Assert.Equal(1, data.GetProperty("unchangedCount").GetInt32());

        var repaired = data.GetProperty("repaired").EnumerateArray().ToList();
        Assert.Equal(new[] { "controlId", "oldBinding", "newBinding", "location", "sectionId", "controlIdCaseDiffers" },
            repaired[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(new (string?, string?, string?, string?, bool)[]
            {
                ("Name", "Name", "name", "My Tab / General", true),
                ("telephone1", "Telephone1", "telephone1", "My Tab / Other", false)
            },
            repaired.Select(r => (r.GetProperty("controlId").GetString(), r.GetProperty("oldBinding").GetString(),
                r.GetProperty("newBinding").GetString(), r.GetProperty("location").GetString(), r.GetProperty("controlIdCaseDiffers").GetBoolean())));
        Assert.Equal("{55555555-5555-5555-5555-555555555555}", repaired[1].GetProperty("sectionId").GetString());

        // A rewritten control is listed once; 'unchanged' holds only what was not rewritten.
        var unchanged = Assert.Single(data.GetProperty("unchanged").EnumerateArray());
        Assert.Equal(new[] { "controlId", "binding", "reason", "location", "sectionId" }, unchanged.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("new_gone", "no-matching-column"), (unchanged.GetProperty("controlId").GetString(), unchanged.GetProperty("reason").GetString()));
        Assert.Single(h!.Writes);
    }

    [Fact]
    [Trait("AC", "AC-78")]
    public async Task Json_OmitsNullControlIdAndSectionId()
    {
        var (_, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedHeaderWithoutId),
            ["--output-format", "Json"]);
        Assert.True(exit == ExitCodes.Success, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var repaired = Assert.Single(doc.RootElement.GetProperty("data").GetProperty("repaired").EnumerateArray());
        Assert.Equal(new[] { "oldBinding", "newBinding", "location", "controlIdCaseDiffers" }, repaired.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Header", repaired.GetProperty("location").GetString());
    }

    [Fact]
    [Trait("AC", "AC-78")]
    public async Task Text_WritesStatusToStderrOnly()
    {
        var (_, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedIdAndUnknownBinding), []);
        Assert.True(exit == ExitCodes.Success, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Repaired 1 control binding(s) on form 'Fixture': the update was submitted and the form, read back, shows each rewritten control with the logical name.", stderr);
        Assert.Contains("Repaired: control 'Name' (My Tab / General) binding 'Name' -> 'name'. Its id also differs", stderr);
        Assert.Contains("is bound to 'New_Gone', which matches no column on 'account'", stderr);
        Assert.Contains("1 binding(s) match no column", stderr);
        Assert.Contains("saved unpublished", stderr);
        Assert.DoesNotContain("remain on the form", stderr);
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task Text_DoesNotClaimAFullRepair_WhileCaseVariantReferencesRemain()
    {
        var (h, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: form =>
            form.Descendants("controlDescription").Elements("customControl").Elements("parameters").Elements("datafieldname")
                .First(p => p.Value == "name").Value = "Name"), []);
        Assert.True(exit == ExitCodes.Success, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("No control binding on form 'Fixture' needed repair", stderr);
        Assert.Contains("1 case-variant reference(s) remain on the form and were not changed", stderr);
        Assert.Contains("custom-control datafieldname parameter", stderr);
        Assert.Empty(h!.Writes);
    }

    [Fact]
    [Trait("AC", "AC-76")]
    public async Task DryRun_Text_SaysWouldRepair_AndNothingWritten()
    {
        var (h, exit, stdout, stderr) = await Run(ct => new ContributorFormHarness(ct, transform: FormBindingRepairTests.MisCasedEverywhere),
            ["--dry-run"], confirm: false);
        Assert.True(exit == ExitCodes.Success, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Dry run: 4 control binding(s) on form 'Fixture' would be repaired. Nothing was written.", stderr);
        Assert.Contains("Would repair: control 'footer_name' (Footer) binding 'NAME' -> 'name'.", stderr);
        Assert.DoesNotContain("Repaired:", stderr);
        Assert.Empty(h!.Writes);
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
        Assert.False(data.GetProperty("readBackVerified").GetBoolean());
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
    [InlineData(false, "--publish")]
    [InlineData(false, "--solution", "fixture_solution")]
    [InlineData(true, "--publish")]
    [InlineData(true, "--solution", "fixture_solution")]
    public async Task DryRun_WithExplicitSideEffect_IsRejectedBeforeConnecting(bool json, params string[] extra)
    {
        var providerCreated = false;
        string[] args = json ? ["--dry-run", "--output-format", "Json", .. extra] : ["--dry-run", .. extra];
        var (_, exit, stdout, stderr) = await Run(ct => { providerCreated = true; return new ContributorFormHarness(ct); }, args);
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.False(providerCreated);
        if (json)
        {
            // The standard JSON error envelope, on stdout.
            using var doc = JsonDocument.Parse(stdout);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            var error = doc.RootElement.GetProperty("error");
            Assert.False(string.IsNullOrEmpty(error.GetProperty("code").GetString()));
            Assert.Contains("--dry-run cannot be combined", error.GetProperty("message").GetString());
        }
        else
        {
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("--dry-run cannot be combined", stderr);
        }
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
    [Trait("AC", "AC-77")]
    public async Task RealConfirmationService_RefusesWithoutConfirmEnv_AndAcceptsTheForwardedValue()
    {
        static EnvironmentConfirmationService Production()
        {
            var config = new Mock<IEnvironmentConfigService>();
            config.Setup(x => x.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EnvironmentConfig { Url = Target, Type = EnvironmentType.Production });
            return new EnvironmentConfirmationService(new EnvironmentConfirmationContext("session", "identity", Target),
                config.Object, new EnvironmentAcknowledgementStore());
        }

        var (refused, refusedExit, refusedOut, _) = await Run(ct => new ContributorFormHarness(ct,
            transform: FormBindingRepairTests.MisCasedEverywhere, confirmation: Production()), ["--output-format", "Json"], confirm: false);
        Assert.NotEqual(ExitCodes.Success, refusedExit);
        using (var doc = JsonDocument.Parse(refusedOut))
            Assert.Equal(EnvironmentConfirmationErrorCodes.Required, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(refused!.Writes);
        Assert.Equal(0, refused.Acquisitions);

        var (accepted, acceptedExit, _, acceptedErr) = await Run(ct => new ContributorFormHarness(ct,
            transform: FormBindingRepairTests.MisCasedEverywhere, confirmation: Production()), ["--output-format", "Json"]);
        Assert.True(acceptedExit == ExitCodes.Success, acceptedErr);
        Assert.Single(accepted!.Writes);

        var (mismatched, mismatchExit, mismatchOut, _) = await Run(ct => new ContributorFormHarness(ct,
            transform: FormBindingRepairTests.MisCasedEverywhere, confirmation: Production()),
            ["--dry-run", "--confirm-env", "https://other.example.test", "--output-format", "Json"], confirm: false);
        Assert.NotEqual(ExitCodes.Success, mismatchExit);
        using (var doc = JsonDocument.Parse(mismatchOut))
            Assert.Equal(EnvironmentConfirmationErrorCodes.TargetMismatch, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, mismatched!.Acquisitions);
    }

    [Fact]
    [Trait("AC", "AC-78")]
    public void Help_DescribesBindingOnlyRepair_AndOptions()
    {
        var command = RepairBindingsCommand.Create();
        Assert.Equal("repair-bindings", command.Name);
        Assert.Contains("Only those attribute values change", command.Description);
        Assert.Contains("read back", command.Description);
        Assert.Contains("left unchanged", command.Description);
        foreach (var option in new[] { "--entity", "--form", "--dry-run", "--publish", "--solution", "--confirm-env", "--profile", "--environment" })
            Assert.Contains(command.Options, o => o.Name == option);
        Assert.Contains("table", command.Options.Single(o => o.Name == "--publish").Description);
    }

    [Fact]
    [Trait("AC", "AC-79")]
    public async Task CaseVariantWarning_FromAddFieldAndLegacyReorder_PointsToRepairBindings()
    {
        static void CaseVariant(XDocument doc) =>
            doc.Descendants("control").First(c => (string?)c.Attribute("datafieldname") == "name").SetAttributeValue("datafieldname", "Name");

        var addField = AddFieldCommand.Create((_, _, _, ct) => Task.FromResult(new ContributorFormHarness(ct, transform: CaseVariant).CreateTextProvider()));
        var reorder = ReorderFieldsCommand.Create((_, _, _, ct) => Task.FromResult(new ContributorFormHarness(ct, transform: CaseVariant).CreateTextProvider()));
        foreach (var (command, args) in new[] { (addField, new[] { "--field", "name" }), (reorder, new[] { "--fields", "name" }) })
        {
            var (exit, stdout, stderr) = await Invoke(command,
                ["--entity", "account", "--form", "Fixture", "--section", "General", .. args, "--confirm-env", Target]);
            Assert.True(exit == ExitCodes.Success, stderr);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("bound to 'Name'", stderr);
            Assert.Contains("'ppds forms repair-bindings' rewrites every control binding on this form", stderr);
            Assert.Contains("--dry-run", stderr);
            Assert.DoesNotContain("forms get --raw", stderr);
        }
    }

    private static async Task<(ContributorFormHarness? Harness, int Exit, string Out, string Error)> Run(
        Func<CancellationToken, ContributorFormHarness> create, string[] args, bool confirm = true)
    {
        // The harness's mocks expect the command's own cancellation token, so it is built inside the provider factory.
        ContributorFormHarness? harness = null;
        var command = RepairBindingsCommand.Create((_, _, _, ct) => Task.FromResult((harness = create(ct)).CreateTextProvider()));
        var all = new List<string> { "--entity", "account", "--form", "Fixture" };
        all.AddRange(args);
        if (confirm) all.AddRange(["--confirm-env", Target]);
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
