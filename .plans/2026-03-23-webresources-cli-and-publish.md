# Web Resources CLI & Publish Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `ppds webresources list|get|url` commands with partial name resolution, and a cross-cutting `ppds publish` command with `--all`, `--type`, and `--solution` flags. Refactor `ppds solutions publish` to alias the new top-level publish.

**Architecture:** Shared `WebResourceNameResolver` handles GUID/exact/partial name matching across all commands. Top-level `PublishCommandGroup` owns all publish logic; domain commands (`webresources publish`, `solutions publish`) are thin aliases that inject `--type`. All commands follow existing CLI patterns (static `Create()` factory, `GlobalOptions`, `ProfileServiceFactory`, `IOutputWriter`).

**Tech Stack:** C# (.NET 8+), System.CommandLine, xUnit

**Specs:** `specs/web-resources.md` (AC-WR-24 through AC-WR-33), `specs/publish.md` (AC-PUB-01 through AC-PUB-13)

---

## File Structure

### New Files

| File | Responsibility |
|------|----------------|
| `src/PPDS.Cli/Commands/WebResources/WebResourcesCommandGroup.cs` | Command group with shared options, registers list/get/url/publish subcommands |
| `src/PPDS.Cli/Commands/WebResources/ListCommand.cs` | List web resources with partial name, solution, type filters |
| `src/PPDS.Cli/Commands/WebResources/GetCommand.cs` | Get web resource content to stdout or file |
| `src/PPDS.Cli/Commands/WebResources/UrlCommand.cs` | Maker portal URL for a web resource |
| `src/PPDS.Cli/Commands/WebResources/WebResourceNameResolver.cs` | GUID → exact → partial name resolution with ambiguity handling |
| `src/PPDS.Cli/Commands/Publish/PublishCommandGroup.cs` | Top-level `ppds publish` with `--all`, `--type`, `--solution`, flag validation |
| `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourcesCommandGroupTests.cs` | Command structure tests |
| `tests/PPDS.Cli.Tests/Commands/WebResources/ListCommandTests.cs` | List command structure tests |
| `tests/PPDS.Cli.Tests/Commands/WebResources/GetCommandTests.cs` | Get command structure tests |
| `tests/PPDS.Cli.Tests/Commands/WebResources/UrlCommandTests.cs` | Url command structure tests |
| `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourceNameResolverTests.cs` | Name resolution unit tests (mocked service) |
| `tests/PPDS.Cli.Tests/Commands/Publish/PublishCommandGroupTests.cs` | Publish command structure + flag validation tests |

### Modified Files

| File | Change |
|------|--------|
| `src/PPDS.Cli/Program.cs:84` | Register `WebResourcesCommandGroup` and `PublishCommandGroup` |
| `src/PPDS.Cli/Commands/Solutions/SolutionsCommandGroup.cs` | Replace `PublishCommand.Create()` with alias to top-level publish |
| `src/PPDS.Cli/Commands/Solutions/PublishCommand.cs` | Delete (replaced by PublishCommandGroup) |
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:260` | Add `WebResource.Ambiguous` error code |
| `tests/PPDS.Cli.Tests/Commands/Solutions/SolutionsCommandGroupTests.cs` | Update publish subcommand test |

---

## Task 1: WebResourceNameResolver

The name resolver is a dependency for all other commands. Build it first with full test coverage.

**Files:**
- Create: `src/PPDS.Cli/Commands/WebResources/WebResourceNameResolver.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourceNameResolverTests.cs`
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:260-273`

- [ ] **Step 1: Add `WebResource.Ambiguous` error code**

In `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`, add inside the `WebResource` class after `PublishFailed`:

```csharp
/// <summary>Multiple web resources matched a partial name.</summary>
public const string Ambiguous = "WebResource.Ambiguous";
```

- [ ] **Step 2: Write failing tests for WebResourceNameResolver**

Create `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourceNameResolverTests.cs`:

```csharp
using PPDS.Cli.Commands.WebResources;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class WebResourceNameResolverTests
{
    private static readonly List<WebResourceInfo> TestResources =
    [
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "new_/scripts/app.js", "App Script", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "new_/scripts/utils.js", "Utils", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "new_/styles/main.css", "Main CSS", 2, false, "Jane", DateTime.UtcNow, "Jane", DateTime.UtcNow),
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "other_/scripts/app.js", "Other App", 3, false, "Josh", DateTime.UtcNow, "Josh", DateTime.UtcNow),
    ];

    [Fact]
    public void Resolve_WithGuid_ReturnsExactMatch()
    {
        var result = WebResourceNameResolver.Resolve("11111111-1111-1111-1111-111111111111", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal("new_/scripts/app.js", result.Matches[0].Name);
    }

    [Fact]
    public void Resolve_WithGuid_NotFound_ReturnsFailure()
    {
        var result = WebResourceNameResolver.Resolve("99999999-9999-9999-9999-999999999999", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Resolve_WithExactName_ReturnsExactMatch()
    {
        var result = WebResourceNameResolver.Resolve("new_/scripts/app.js", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Matches[0].Id);
    }

    [Fact]
    public void Resolve_WithPartialName_SingleMatch_ReturnsSuccess()
    {
        var result = WebResourceNameResolver.Resolve("utils.js", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
        Assert.Equal("new_/scripts/utils.js", result.Matches[0].Name);
    }

    [Fact]
    public void Resolve_WithPartialName_MultipleMatches_ReturnsAllMatches()
    {
        var result = WebResourceNameResolver.Resolve("app.js", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void Resolve_WithPartialName_NoMatch_ReturnsFailure()
    {
        var result = WebResourceNameResolver.Resolve("notfound.js", TestResources);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var result = WebResourceNameResolver.Resolve("MAIN.CSS", TestResources);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void Filter_WithPartialName_ReturnsAllMatches()
    {
        var result = WebResourceNameResolver.Filter("app.js", TestResources);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_WithPrefix_ReturnsAllUnderPrefix()
    {
        var result = WebResourceNameResolver.Filter("new_/scripts/", TestResources);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_WithNoMatch_ReturnsEmpty()
    {
        var result = WebResourceNameResolver.Filter("notfound", TestResources);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResourceNameResolverTests" -v q`
Expected: Build error — `WebResourceNameResolver` does not exist.

- [ ] **Step 4: Implement WebResourceNameResolver**

Create `src/PPDS.Cli/Commands/WebResources/WebResourceNameResolver.cs`:

```csharp
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Resolves web resource identifiers (GUID, exact name, partial name) against a list of resources.
/// Used by list, get, url, and publish commands.
/// </summary>
public static class WebResourceNameResolver
{
    /// <summary>
    /// Resolution result for single-resource commands (get, url, publish).
    /// IsSuccess is true only when exactly one match is found.
    /// </summary>
    public sealed record ResolveResult(bool IsSuccess, IReadOnlyList<WebResourceInfo> Matches);

    /// <summary>
    /// Resolves a single identifier to a web resource. Returns success only for exactly one match.
    /// Resolution order: GUID → exact name → partial match (ends with).
    /// </summary>
    public static ResolveResult Resolve(string identifier, IReadOnlyList<WebResourceInfo> resources)
    {
        // 1. Try GUID
        if (Guid.TryParse(identifier, out var guid))
        {
            var byId = resources.Where(r => r.Id == guid).ToList();
            return new ResolveResult(byId.Count == 1, byId);
        }

        // 2. Try exact name (case-insensitive)
        var exact = resources
            .Where(r => r.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return new ResolveResult(exact.Count == 1, exact);
        }

        // 3. Partial match: name ends with /identifier or equals identifier
        var partial = resources
            .Where(r => r.Name.EndsWith("/" + identifier, StringComparison.OrdinalIgnoreCase)
                     || r.Name.EndsWith(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ResolveResult(partial.Count == 1, partial);
    }

    /// <summary>
    /// Filters resources by partial name match. For list commands where multiple matches are expected.
    /// Matches: exact name, name contains, name starts with (prefix match).
    /// </summary>
    public static IReadOnlyList<WebResourceInfo> Filter(string pattern, IReadOnlyList<WebResourceInfo> resources)
    {
        return resources
            .Where(r => r.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResourceNameResolverTests" -v q`
Expected: All 10 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Commands/WebResources/WebResourceNameResolver.cs tests/PPDS.Cli.Tests/Commands/WebResources/WebResourceNameResolverTests.cs src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs
git commit -m "feat(webresources): add WebResourceNameResolver with GUID/exact/partial name resolution"
```

---

## Task 2: WebResourcesCommandGroup + ListCommand

**Files:**
- Create: `src/PPDS.Cli/Commands/WebResources/WebResourcesCommandGroup.cs`
- Create: `src/PPDS.Cli/Commands/WebResources/ListCommand.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourcesCommandGroupTests.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/WebResources/ListCommandTests.cs`
- Modify: `src/PPDS.Cli/Program.cs:84`

- [ ] **Step 1: Write failing tests for command structure**

Create `tests/PPDS.Cli.Tests/Commands/WebResources/WebResourcesCommandGroupTests.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class WebResourcesCommandGroupTests
{
    private readonly Command _command;

    public WebResourcesCommandGroupTests()
    {
        _command = WebResourcesCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("webresources", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("web resource", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasGetSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "get");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUrlSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "url");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasPublishSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "publish");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", WebResourcesCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", WebResourcesCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", WebResourcesCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", WebResourcesCommandGroup.EnvironmentOption.Aliases);
    }
}
```

Create `tests/PPDS.Cli.Tests/Commands/WebResources/ListCommandTests.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class ListCommandTests
{
    private readonly Command _command;

    public ListCommandTests()
    {
        _command = ListCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("list", _command.Name);
    }

    [Fact]
    public void Create_HasOptionalNamePatternArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "name-pattern");
        Assert.NotNull(arg);
        // Optional argument — Arity.ZeroOrOne
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTopOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--top");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResourcesCommandGroupTests|FullyQualifiedName~WebResources.ListCommandTests" -v q`
Expected: Build error — classes don't exist.

- [ ] **Step 3: Implement WebResourcesCommandGroup**

Create `src/PPDS.Cli/Commands/WebResources/WebResourcesCommandGroup.cs`:

```csharp
using System.CommandLine;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Web resources command group for managing Dataverse web resources.
/// </summary>
public static class WebResourcesCommandGroup
{
    /// <summary>
    /// Profile option for authentication.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for target environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'webresources' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("webresources", "Manage Dataverse web resources: list, get, url, publish");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(UrlCommand.Create());
        command.Subcommands.Add(PublishAliasCommand.Create());

        return command;
    }
}
```

Note: `PublishAliasCommand` is fully implemented in Task 6. Create a minimal stub now for compilation:

```csharp
// src/PPDS.Cli/Commands/WebResources/PublishAliasCommand.cs (stub — replaced in Task 6)
using System.CommandLine;

namespace PPDS.Cli.Commands.WebResources;

public static class PublishAliasCommand
{
    public static Command Create() => new("publish", "Publish web resources (stub)");
}
```

- [ ] **Step 4: Implement ListCommand**

Create `src/PPDS.Cli/Commands/WebResources/ListCommand.cs`:

```csharp
using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// List web resources with optional filters.
/// </summary>
public static class ListCommand
{
    /// <summary>
    /// Type shortcut mappings. "text" and "image" expand to multiple type codes.
    /// Individual types map to their type code.
    /// </summary>
    private static readonly Dictionary<string, int[]> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = [1, 2, 3, 4, 9, 11, 12],     // HTML, CSS, JS, XML, XSL, SVG, RESX
        ["image"] = [5, 6, 7, 10, 11],            // PNG, JPG, GIF, ICO, SVG
        ["data"] = [4, 12],                        // XML, RESX
        ["html"] = [1],
        ["css"] = [2],
        ["js"] = [3], ["javascript"] = [3],
        ["xml"] = [4],
        ["png"] = [5],
        ["jpg"] = [6], ["jpeg"] = [6],
        ["gif"] = [7],
        ["xap"] = [8],
        ["xsl"] = [9], ["xslt"] = [9],
        ["ico"] = [10],
        ["svg"] = [11],
        ["resx"] = [12],
    };

    public static Command Create()
    {
        var namePatternArgument = new Argument<string?>("name-pattern")
        {
            Description = "Filter by partial name match (e.g., 'app.js', 'new_/scripts/')",
            Arity = ArgumentArity.ZeroOrOne
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution unique name"
        };

        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by type: text, image, data, or specific type (js, css, html, xml, png, etc.)"
        };

        var topOption = new Option<int?>("--top")
        {
            Description = "Maximum number of results (default: 5000)"
        };

        var command = new Command("list", "List web resources in the environment")
        {
            namePatternArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            solutionOption,
            typeOption,
            topOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var namePattern = parseResult.GetValue(namePatternArgument);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(solutionOption);
            var type = parseResult.GetValue(typeOption);
            var top = parseResult.GetValue(topOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(namePattern, profile, environment, solution, type, top, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? namePattern,
        string? profile,
        string? environment,
        string? solution,
        string? type,
        int? top,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate --type if provided
        int[]? typeCodes = null;
        if (type != null)
        {
            if (!TypeMap.TryGetValue(type, out typeCodes))
            {
                var error = new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    $"Unknown type '{type}'. Supported: text, image, data, js, css, html, xml, png, jpg, gif, svg, ico, xsl, resx",
                    null,
                    type);
                writer.WriteError(error);
                return ExitCodes.InvalidArguments;
            }
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve solution name to ID if provided
            Guid? solutionId = null;
            if (solution != null)
            {
                var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
                var solutionInfo = await solutionService.GetAsync(solution, cancellationToken);
                if (solutionInfo == null)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Solution '{solution}' not found.",
                        null,
                        solution);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                solutionId = solutionInfo.Id;
            }

            // Use textOnly if type filter is specifically "text"
            var textOnly = type != null && type.Equals("text", StringComparison.OrdinalIgnoreCase);

            var resources = await webResourceService.ListAsync(
                solutionId: solutionId,
                textOnly: textOnly,
                top: top ?? 5000,
                cancellationToken: cancellationToken);

            // Apply type filter (for specific types, not "text" shortcut which is handled server-side)
            if (typeCodes != null && !textOnly)
            {
                resources = resources.Where(r => typeCodes.Contains(r.WebResourceType)).ToList();
            }

            // Apply name pattern filter
            if (!string.IsNullOrEmpty(namePattern))
            {
                resources = WebResourceNameResolver.Filter(namePattern, resources).ToList();
            }

            if (resources.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Resources = [] });
                }
                else
                {
                    Console.Error.WriteLine("No web resources found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Resources = resources.Select(r => new WebResourceOutput
                    {
                        Id = r.Id,
                        Name = r.Name,
                        DisplayName = r.DisplayName,
                        Type = r.TypeName,
                        WebResourceType = r.WebResourceType,
                        IsManaged = r.IsManaged,
                        IsTextType = r.IsTextType,
                        CreatedBy = r.CreatedByName,
                        CreatedOn = r.CreatedOn,
                        ModifiedBy = r.ModifiedByName,
                        ModifiedOn = r.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"{"Name",-50} {"Type",-12} {"Managed",-10} {"Modified On",-20} {"Modified By"}");
                Console.Error.WriteLine(new string('-', 112));

                foreach (var r in resources)
                {
                    var name = Truncate(r.Name, 50);
                    var type_ = Truncate(r.TypeName, 12);
                    var managed = r.IsManaged ? "Managed" : "Unmanaged";
                    var modified = r.ModifiedOn?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                    var modifiedBy = Truncate(r.ModifiedByName ?? "-", 20);

                    Console.Error.WriteLine($"{name,-50} {type_,-12} {managed,-10} {modified,-20} {modifiedBy}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {resources.Count} web resource(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing web resources", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("resources")]
        public List<WebResourceOutput> Resources { get; set; } = [];
    }

    private sealed class WebResourceOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("webResourceType")]
        public int WebResourceType { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("isTextType")]
        public bool IsTextType { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
```

- [ ] **Step 5: Register in Program.cs**

In `src/PPDS.Cli/Program.cs`, add after the `RolesCommandGroup` line (~line 92):

```csharp
rootCommand.Subcommands.Add(WebResourcesCommandGroup.Create());
```

Note: The `PublishAliasCommand` stub may need to exist for compilation. Create a minimal stub if needed — it will be fully implemented in Task 5.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResourcesCommandGroupTests|FullyQualifiedName~WebResources.ListCommandTests" -v q`
Expected: All tests pass.

- [ ] **Step 7: Run full build to verify no compilation errors**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/PPDS.Cli/Commands/WebResources/WebResourcesCommandGroup.cs src/PPDS.Cli/Commands/WebResources/ListCommand.cs src/PPDS.Cli/Commands/WebResources/PublishAliasCommand.cs src/PPDS.Cli/Program.cs tests/PPDS.Cli.Tests/Commands/WebResources/WebResourcesCommandGroupTests.cs tests/PPDS.Cli.Tests/Commands/WebResources/ListCommandTests.cs
git commit -m "feat(webresources): add webresources command group with list command"
```

---

## Task 3: GetCommand

**Files:**
- Create: `src/PPDS.Cli/Commands/WebResources/GetCommand.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/WebResources/GetCommandTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/PPDS.Cli.Tests/Commands/WebResources/GetCommandTests.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class GetCommandTests
{
    private readonly Command _command;

    public GetCommandTests()
    {
        _command = GetCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("get", _command.Name);
    }

    [Fact]
    public void Create_HasNameArgument()
    {
        Assert.Single(_command.Arguments);
        Assert.Equal("name", _command.Arguments[0].Name);
    }

    [Fact]
    public void Create_HasUnpublishedOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--unpublished");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasOutputOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResources.GetCommandTests" -v q`
Expected: Build error.

- [ ] **Step 3: Implement GetCommand**

Create `src/PPDS.Cli/Commands/WebResources/GetCommand.cs`:

```csharp
using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Get web resource content by name or ID.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Web resource name, partial name, or GUID"
        };

        var unpublishedOption = new Option<bool>("--unpublished")
        {
            Description = "Get the unpublished (latest draft) version instead of published"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Write content to file instead of stdout"
        };

        var command = new Command("get", "Get web resource content")
        {
            nameArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            unpublishedOption,
            outputOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var unpublished = parseResult.GetValue(unpublishedOption);
            var output = parseResult.GetValue(outputOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, unpublished, output, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string? profile,
        string? environment,
        bool unpublished,
        string? outputPath,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            // Resolve name to ID
            var resources = await webResourceService.ListAsync(cancellationToken: cancellationToken);
            var resolveResult = WebResourceNameResolver.Resolve(name, resources);

            if (!resolveResult.IsSuccess)
            {
                if (resolveResult.Matches.Count == 0)
                {
                    var error = new StructuredError(
                        ErrorCodes.WebResource.NotFound,
                        $"Web resource '{name}' not found.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                else
                {
                    // Ambiguous
                    var matchNames = string.Join("\n  ", resolveResult.Matches.Select(m => m.Name));
                    var error = new StructuredError(
                        ErrorCodes.WebResource.Ambiguous,
                        $"Multiple web resources match '{name}':\n  {matchNames}\n\nSpecify a more complete name to narrow the match.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.InvalidArguments;
                }
            }

            var resource = resolveResult.Matches[0];

            // Check binary to stdout
            if (!resource.IsTextType && outputPath == null && !globalOptions.IsJsonMode)
            {
                var error = new StructuredError(
                    ErrorCodes.WebResource.NotEditable,
                    $"Web resource '{resource.Name}' is a {resource.TypeName} file (binary). Use --output <path> to save to a file.",
                    null,
                    resource.Name);
                writer.WriteError(error);
                return ExitCodes.InvalidArguments;
            }

            // Fetch content — published by default, unpublished if requested
            var content = await webResourceService.GetContentAsync(
                resource.Id,
                published: !unpublished,
                cancellationToken: cancellationToken);

            if (content?.Content == null)
            {
                var error = new StructuredError(
                    ErrorCodes.WebResource.NotFound,
                    $"Web resource '{resource.Name}' has no content.",
                    null,
                    resource.Name);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new GetOutput
                {
                    Id = resource.Id,
                    Name = resource.Name,
                    Type = resource.TypeName,
                    Content = content.Content,
                    ModifiedOn = content.ModifiedOn
                };
                writer.WriteSuccess(output);
            }
            else if (outputPath != null)
            {
                await File.WriteAllTextAsync(outputPath, content.Content, cancellationToken);
                Console.Error.WriteLine($"Written to {outputPath} ({content.Content.Length} bytes)");
            }
            else
            {
                // Text content to stdout (pipeable)
                Console.WriteLine(content.Content);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class GetOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResources.GetCommandTests" -v q`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/WebResources/GetCommand.cs tests/PPDS.Cli.Tests/Commands/WebResources/GetCommandTests.cs
git commit -m "feat(webresources): add get command with content output to stdout or file"
```

---

## Task 4: UrlCommand

**Files:**
- Create: `src/PPDS.Cli/Commands/WebResources/UrlCommand.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/WebResources/UrlCommandTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/PPDS.Cli.Tests/Commands/WebResources/UrlCommandTests.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class UrlCommandTests
{
    private readonly Command _command;

    public UrlCommandTests()
    {
        _command = UrlCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("url", _command.Name);
    }

    [Fact]
    public void Create_HasNameArgument()
    {
        Assert.Single(_command.Arguments);
        Assert.Equal("name", _command.Arguments[0].Name);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResources.UrlCommandTests" -v q`
Expected: Build error.

- [ ] **Step 3: Implement UrlCommand**

Create `src/PPDS.Cli/Commands/WebResources/UrlCommand.cs`:

```csharp
using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Get the Maker portal URL for a web resource.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Web resource name, partial name, or GUID"
        };

        var command = new Command("url", "Get the Maker portal URL for a web resource")
        {
            nameArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            // Resolve name to ID
            var resources = await webResourceService.ListAsync(cancellationToken: cancellationToken);
            var resolveResult = WebResourceNameResolver.Resolve(name, resources);

            if (!resolveResult.IsSuccess)
            {
                if (resolveResult.Matches.Count == 0)
                {
                    var error = new StructuredError(
                        ErrorCodes.WebResource.NotFound,
                        $"Web resource '{name}' not found.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                else
                {
                    var matchNames = string.Join("\n  ", resolveResult.Matches.Select(m => m.Name));
                    var error = new StructuredError(
                        ErrorCodes.WebResource.Ambiguous,
                        $"Multiple web resources match '{name}':\n  {matchNames}\n\nSpecify a more complete name to narrow the match.",
                        null,
                        name);
                    writer.WriteError(error);
                    return ExitCodes.InvalidArguments;
                }
            }

            var resource = resolveResult.Matches[0];
            var makerUrl = BuildMakerUrl(connectionInfo.EnvironmentUrl, resource.Id);

            if (globalOptions.IsJsonMode)
            {
                var output = new UrlOutput
                {
                    Name = resource.Name,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine(makerUrl);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting URL for web resource '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string BuildMakerUrl(string environmentUrl, Guid webResourceId)
    {
        var uri = new Uri(environmentUrl);
        // Web resource editor URL in the classic interface
        return $"{uri.Scheme}://{uri.Host}/main.aspx?appid=&pagetype=webresourceedit&id={{{webResourceId}}}";
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResources.UrlCommandTests" -v q`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/WebResources/UrlCommand.cs tests/PPDS.Cli.Tests/Commands/WebResources/UrlCommandTests.cs
git commit -m "feat(webresources): add url command for Maker portal links"
```

---

## Task 5: PublishCommandGroup (Top-Level)

The core publish command with `--all`, `--type`, `--solution`, and flag validation.

**Files:**
- Create: `src/PPDS.Cli/Commands/Publish/PublishCommandGroup.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/Publish/PublishCommandGroupTests.cs`
- Modify: `src/PPDS.Cli/Program.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/PPDS.Cli.Tests/Commands/Publish/PublishCommandGroupTests.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Publish;

public class PublishCommandGroupTests
{
    private readonly Command _command;

    public PublishCommandGroupTests()
    {
        _command = PublishCommandGroup.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("publish", _command.Name);
    }

    [Fact]
    public void Create_HasAllOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--all");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTypeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasNamesArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "names");
        Assert.NotNull(arg);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }

    [Fact]
    public void Create_HasValidator()
    {
        // The command should have validators for flag combination rules
        Assert.NotEmpty(_command.Validators);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~PublishCommandGroupTests" -v q`
Expected: Build error.

- [ ] **Step 3: Implement PublishCommandGroup**

Create `src/PPDS.Cli/Commands/Publish/PublishCommandGroup.cs`:

```csharp
using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Publish;

/// <summary>
/// Top-level publish command for Dataverse customizations.
/// Supports --all (PublishAllXml), --type with specific resources, and --solution scoping.
/// </summary>
public static class PublishCommandGroup
{
    private static readonly Option<bool> AllOption = new("--all")
    {
        Description = "Publish all customizations (PublishAllXml). Cannot combine with other flags."
    };

    private static readonly Option<string?> TypeOption = new("--type", "-t")
    {
        Description = "Component type to publish. Required when specifying resources or --solution. Supported: webresource"
    };

    private static readonly Option<string?> SolutionOption = new("--solution", "-s")
    {
        Description = "Publish all components of the specified type in this solution"
    };

    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    private static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the top-level 'publish' command. Also used by domain aliases.
    /// </summary>
    public static Command Create()
    {
        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Web resource names, partial names, or GUIDs to publish",
            Arity = ArgumentArity.ZeroOrMore
        };

        var command = new Command("publish", "Publish Dataverse customizations")
        {
            namesArgument,
            AllOption,
            TypeOption,
            SolutionOption,
            ProfileOption,
            EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        // Flag combination validation
        command.Validators.Add(result =>
        {
            var all = result.GetValue(AllOption);
            var type = result.GetValue(TypeOption);
            var solution = result.GetValue(SolutionOption);
            var names = result.GetValue(namesArgument) ?? [];

            if (all)
            {
                if (type != null)
                    result.AddError("--all publishes all customizations. Remove --type or use --solution to scope.");
                if (solution != null)
                    result.AddError("--all publishes all customizations. Remove --all to scope by solution.");
                if (names.Length > 0)
                    result.AddError("--all publishes all customizations. Remove --all to publish specific resources.");
            }
            else if (names.Length > 0 && type == null)
            {
                result.AddError("--type is required when specifying resources. Example: ppds publish --type webresource app.js");
            }
            else if (solution != null && type == null)
            {
                result.AddError("--type is required with --solution. Supported types: webresource");
            }
            else if (!all && names.Length == 0 && solution == null)
            {
                // Bare "ppds publish" with no flags — we let it through and show help in execute
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var names = parseResult.GetValue(namesArgument) ?? [];
            var all = parseResult.GetValue(AllOption);
            var type = parseResult.GetValue(TypeOption);
            var solution = parseResult.GetValue(SolutionOption);
            var profile = parseResult.GetValue(ProfileOption);
            var environment = parseResult.GetValue(EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(names, all, type, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Core execution — also called by alias commands that pre-set type/all.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        string[] names,
        bool all,
        string? type,
        string? solution,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Bare command with no actionable input
        if (!all && names.Length == 0 && solution == null)
        {
            Console.Error.WriteLine("Usage: ppds publish --all | --type <type> <names...> | --type <type> --solution <name>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --all                Publish all customizations (PublishAllXml)");
            Console.Error.WriteLine("  --type <type>        Component type (supported: webresource)");
            Console.Error.WriteLine("  --solution <name>    Scope to components in a solution (requires --type)");
            return ExitCodes.InvalidArguments;
        }

        // Validate type if provided
        if (type != null && !type.Equals("webresource", StringComparison.OrdinalIgnoreCase))
        {
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"Unsupported type '{type}'. Supported types: webresource",
                null,
                type);
            writer.WriteError(error);
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            if (all)
            {
                return await PublishAllAsync(serviceProvider, writer, globalOptions, cancellationToken);
            }
            else
            {
                return await PublishWebResourcesAsync(
                    serviceProvider, names, solution, writer, globalOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "publishing customizations", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> PublishAllAsync(
        ServiceProvider serviceProvider,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        // Use ISolutionService for PublishAllXml — this is a platform-level operation,
        // not web-resource-specific. Matches original ppds solutions publish behavior.
        var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine("Publishing all customizations...");
        }

        var startTime = DateTime.UtcNow;
        await solutionService.PublishAllAsync(cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new PublishAllOutput
            {
                Success = true,
                DurationSeconds = duration.TotalSeconds
            });
        }
        else
        {
            Console.Error.WriteLine($"Published successfully in {duration.TotalSeconds:F1} seconds.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> PublishWebResourcesAsync(
        ServiceProvider serviceProvider,
        string[] names,
        string? solution,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();

        // Resolve solution if provided
        Guid? solutionId = null;
        if (solution != null)
        {
            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
            var solutionInfo = await solutionService.GetAsync(solution, cancellationToken);
            if (solutionInfo == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Solution '{solution}' not found.",
                    null,
                    solution);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }
            solutionId = solutionInfo.Id;
        }

        // Get all resources (filtered by solution if provided) for name resolution
        var resources = await webResourceService.ListAsync(
            solutionId: solutionId,
            cancellationToken: cancellationToken);

        List<Guid> idsToPublish;

        if (names.Length > 0)
        {
            // Resolve each name
            idsToPublish = [];
            foreach (var name in names)
            {
                var resolveResult = WebResourceNameResolver.Resolve(name, resources);
                if (!resolveResult.IsSuccess)
                {
                    if (resolveResult.Matches.Count == 0)
                    {
                        var error = new StructuredError(
                            ErrorCodes.WebResource.NotFound,
                            $"Web resource '{name}' not found.",
                            null,
                            name);
                        writer.WriteError(error);
                        return ExitCodes.NotFoundError;
                    }
                    else
                    {
                        var matchNames = string.Join("\n  ", resolveResult.Matches.Select(m => m.Name));
                        var error = new StructuredError(
                            ErrorCodes.WebResource.Ambiguous,
                            $"Multiple web resources match '{name}':\n  {matchNames}\n\nSpecify a more complete name to narrow the match.",
                            null,
                            name);
                        writer.WriteError(error);
                        return ExitCodes.InvalidArguments;
                    }
                }
                idsToPublish.Add(resolveResult.Matches[0].Id);
            }
        }
        else
        {
            // --solution without names: publish all web resources in solution
            idsToPublish = resources.Select(r => r.Id).ToList();
        }

        if (idsToPublish.Count == 0)
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine("No web resources to publish.");
            }
            else
            {
                writer.WriteSuccess(new PublishResourcesOutput
                {
                    PublishedCount = 0,
                    DurationSeconds = 0
                });
            }
            return ExitCodes.Success;
        }

        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine($"Publishing {idsToPublish.Count} web resource(s)...");
        }

        var startTime = DateTime.UtcNow;
        var publishedCount = await webResourceService.PublishAsync(idsToPublish, cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new PublishResourcesOutput
            {
                PublishedCount = publishedCount,
                DurationSeconds = duration.TotalSeconds
            });
        }
        else
        {
            Console.Error.WriteLine($"Published {publishedCount} web resource(s) in {duration.TotalSeconds:F1} seconds.");
        }

        return ExitCodes.Success;
    }

    #region Output Models

    private sealed class PublishAllOutput
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    private sealed class PublishResourcesOutput
    {
        [JsonPropertyName("publishedCount")]
        public int PublishedCount { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    #endregion
}
```

- [ ] **Step 4: Register in Program.cs**

In `src/PPDS.Cli/Program.cs`, add after the `WebResourcesCommandGroup` line:

```csharp
rootCommand.Subcommands.Add(PublishCommandGroup.Create());
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~PublishCommandGroupTests" -v q`
Expected: All tests pass.

- [ ] **Step 6: Run full build**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/PPDS.Cli/Commands/Publish/PublishCommandGroup.cs tests/PPDS.Cli.Tests/Commands/Publish/PublishCommandGroupTests.cs src/PPDS.Cli/Program.cs
git commit -m "feat(publish): add top-level publish command with --all, --type, --solution"
```

---

## Task 6: Publish Alias Commands + Solutions Refactor

Wire `ppds webresources publish` as an alias and refactor `ppds solutions publish` to delegate to the top-level command.

**Files:**
- Create: `src/PPDS.Cli/Commands/WebResources/PublishAliasCommand.cs`
- Modify: `src/PPDS.Cli/Commands/Solutions/PublishCommand.cs` (rewrite as alias)
- Modify: `tests/PPDS.Cli.Tests/Commands/Solutions/SolutionsCommandGroupTests.cs`

- [ ] **Step 1: Create WebResources PublishAliasCommand**

Create `src/PPDS.Cli/Commands/WebResources/PublishAliasCommand.cs`:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Alias for 'ppds publish --type webresource'. Auto-injects --type.
/// </summary>
public static class PublishAliasCommand
{
    public static Command Create()
    {
        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Web resource names, partial names, or GUIDs to publish",
            Arity = ArgumentArity.ZeroOrMore
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Publish all web resources in this solution"
        };

        var command = new Command("publish", "Publish web resources (alias for ppds publish --type webresource)")
        {
            namesArgument,
            solutionOption,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var names = parseResult.GetValue(namesArgument) ?? [];
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // Delegate to top-level publish with --type webresource injected
            return await PublishCommandGroup.ExecuteAsync(
                names,
                all: false,
                type: "webresource",
                solution: solution,
                profile: profile,
                environment: environment,
                globalOptions: globalOptions,
                cancellationToken: cancellationToken);
        });

        return command;
    }
}
```

- [ ] **Step 2: Rewrite Solutions PublishCommand as alias**

Replace `src/PPDS.Cli/Commands/Solutions/PublishCommand.cs` with:

```csharp
using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Publish all customizations (alias for ppds publish --all).
/// </summary>
public static class PublishCommand
{
    public static Command Create()
    {
        var command = new Command("publish", "Publish all customizations (alias for ppds publish --all)")
        {
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // Delegate to top-level publish with --all
            return await PublishCommandGroup.ExecuteAsync(
                names: [],
                all: true,
                type: null,
                solution: null,
                profile: profile,
                environment: environment,
                globalOptions: globalOptions,
                cancellationToken: cancellationToken);
        });

        return command;
    }
}
```

- [ ] **Step 3: Run existing solutions tests to verify alias doesn't break structure**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~SolutionsCommandGroupTests" -v q`
Expected: All tests pass (same command name "publish", same subcommand count of 7).

- [ ] **Step 4: Run full build and all tests**

Run: `dotnet build PPDS.sln -v q && dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: Build succeeds. All unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/WebResources/PublishAliasCommand.cs src/PPDS.Cli/Commands/Solutions/PublishCommand.cs
git commit -m "feat(publish): wire webresources publish alias and refactor solutions publish to delegate"
```

---

## Task 7: Specs Commit + Final Verification

Commit the specs we wrote during the design session and do a final build verification.

- [ ] **Step 1: Run full test suite**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass.

- [ ] **Step 2: Verify all new tests exist and pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~WebResources|FullyQualifiedName~PublishCommandGroupTests" -v q`
Expected: All WebResources + Publish tests pass.

- [ ] **Step 3: Commit specs**

```bash
git add specs/web-resources.md specs/publish.md docs/plans/2026-03-23-webresources-cli-and-publish.md
git commit -m "docs: add web resources CLI and publish specs and implementation plan"
```

---

## Summary

| Task | What | Files | Depends On |
|------|------|-------|------------|
| 1 | WebResourceNameResolver | 2 new + 1 modified | — |
| 2 | CommandGroup + ListCommand | 4 new + 1 modified | Task 1 |
| 3 | GetCommand | 2 new | Task 1, 2 |
| 4 | UrlCommand | 2 new | Task 1, 2 |
| 5 | PublishCommandGroup (top-level) | 2 new + 1 modified | Task 1 |
| 6 | Publish aliases + Solutions refactor | 1 new + 1 modified + 1 test modified | Task 5 |
| 7 | Specs + final verification | 3 files | All |

Tasks 3 and 4 are independent and can run in parallel. Task 5 depends only on Task 1. Task 6 depends on Tasks 2 and 5.
