# PPDS.Plugins

Attribute-driven plugin registration for Dataverse. Decorate your plugin classes with `[PluginStep]`, `[PluginImage]`, and `[CustomApi]` attributes, and let the PPDS CLI extract the metadata from your compiled plugin assembly and deploy the registration declaratively. Source-of-truth lives next to your code, registrations diff cleanly in pull requests, and deployments are repeatable across environments — no more manual clicks in the Plugin Registration Tool.

## Installation

```bash
dotnet add package PPDS.Plugins
```

Target framework: `net462` (Dataverse plugin sandbox).

## Quick Start

```csharp
using Microsoft.Xrm.Sdk;
using PPDS.Plugins;

[PluginStep(
    Message = "Update",
    EntityLogicalName = "account",
    Stage = PluginStage.PostOperation,
    Mode = PluginMode.Synchronous,
    FilteringAttributes = "name,telephone1")]
[PluginImage(
    ImageType = PluginImageType.PreImage,
    Name = "PreImage",
    Attributes = "name,telephone1")]
public class AccountUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var preImage = context.PreEntityImages["PreImage"];
        // ... plugin logic
    }
}
```

Build your plugin project, then deploy with the PPDS CLI:

```bash
ppds plugins deploy --assembly bin/Release/net462/MyPlugins.dll
```

The CLI reflects over the assembly, extracts every `[PluginStep]`, `[PluginImage]`, and `[CustomApi]` attribute, and creates or updates the corresponding Dataverse plugin type, step, image, and Custom API records.

## Attributes

| Attribute | Applies To | Purpose |
|-----------|------------|---------|
| `[PluginStep]` | Plugin class | SDK message registration (message, entity, stage, mode, filtering attributes, rank, etc.) |
| `[PluginImage]` | Plugin class | Pre-image or post-image for a step |
| `[CustomApi]` | Plugin class | Custom API declaration (unique name, binding, ownership, allowed callers) |
| `[CustomApiParameter]` | Plugin class | Request and response parameters for a Custom API |

Multiple attributes can be applied to the same class for plugins that handle multiple messages or entities. See the XML documentation in the source for the full property list and semantics: [src/PPDS.Plugins/Attributes/](https://github.com/joshsmithxrm/power-platform-developer-suite/tree/main/src/PPDS.Plugins/Attributes).

## Why This Over the Plugin Registration Tool?

- **Source-controlled.** Registration configuration lives in C#, versioned alongside the plugin logic it describes.
- **Reviewable.** Changes to registrations show up in pull request diffs — no out-of-band configuration drift.
- **Deterministic deployments.** The same assembly deploys identically across dev, test, and prod environments.
- **CI-friendly.** `ppds plugins deploy` runs headless with a service principal; no interactive UI required.
- **Diff-aware.** `ppds plugins diff` shows exactly what will change before you deploy.

## Related CLI Commands

| Command | Purpose |
|---------|---------|
| `ppds plugins extract` | Dump attribute metadata from a plugin assembly as JSON |
| `ppds plugins diff` | Compare attribute metadata against what's registered in Dataverse |
| `ppds plugins deploy` | Apply attribute metadata to the connected environment |
| `ppds plugins list` | List registered plugin assemblies and steps |
| `ppds plugins clean` | Remove plugin registrations no longer present in the assembly |

## Target Frameworks

- `net462` (required for the Dataverse sandbox)

## Report an Issue

Found a bug or have a feature request? [Report an Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues).

## License

MIT License
