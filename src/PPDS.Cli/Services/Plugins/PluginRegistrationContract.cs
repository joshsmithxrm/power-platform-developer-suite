using System.Text.Json;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Applies the explicitly selected registration-file contract at service boundaries.</summary>
internal static class PluginRegistrationContract
{
    internal const string LegacyVersion = "1.0";
    internal const string ReconciliationVersion = "2.0";

    internal static RegistrationOperationIntent ResolveVersion(string? version) => version switch
    {
        LegacyVersion => RegistrationOperationIntent.Deploy,
        ReconciliationVersion => RegistrationOperationIntent.Reconcile,
        _ => throw new PpdsException(ErrorCodes.Validation.InvalidValue,
            "Unsupported registration version. Use 1.0 for legacy deployment or 2.0 for nonsecret reconciliation.")
    };

    internal static RegistrationOperationIntent Apply(PluginRegistrationConfig config)
    {
        var intent = ValidateForVersion(config);
        // Commit operational metadata only after the complete graph passes validation.
        foreach (var assembly in config.Assemblies)
        {
            assembly.OperationIntent = intent;
            foreach (var step in assembly.Types.SelectMany(t => t.Steps))
            {
                step.OperationIntent = intent;
                foreach (var image in step.Images) image.OperationIntent = intent;
            }
        }
        return intent;
    }

    /// <summary>Validates a source and selected output contract without changing either graph or version.</summary>
    internal static RegistrationOperationIntent ValidateForVersion(PluginRegistrationConfig config, string? targetVersion = null)
    {
        if (config is null) throw InvalidStructure();
        var sourceIntent = ResolveVersion(config.Version);
        var intent = targetVersion == null ? sourceIntent : ResolveVersion(targetVersion);
        // A target override cannot bypass validation of an already-versioned source.
        var validationIntent = sourceIntent == RegistrationOperationIntent.Reconcile ? sourceIntent : intent;
        if (config.Assemblies is null) throw InvalidStructure();
        ValidateUnknownFields(config.ExtensionData, "root", validationIntent);
        foreach (var assembly in config.Assemblies)
        {
            if (assembly?.Types is null) throw InvalidStructure();
            ValidateUnknownFields(assembly.ExtensionData, "assembly", validationIntent);
            foreach (var type in assembly.Types)
            {
                if (type?.Steps is null) throw InvalidStructure();
                ValidateUnknownFields(type.ExtensionData, "type", validationIntent);
                foreach (var step in type.Steps)
                {
                    if (step?.Images is null || step.ResetFields is null) throw InvalidStructure();
                    ValidateUnknownFields(step.ExtensionData, "step", validationIntent);
                    foreach (var image in step.Images)
                    {
                        if (image is null || image.ResetFields is null) throw InvalidStructure();
                        ValidateUnknownFields(image.ExtensionData, "image", validationIntent);
                    }
                }
            }
        }
        return intent;
    }

    internal static RegistrationOperationIntent FileIntent(RegistrationOperationIntent intent) =>
        intent == RegistrationOperationIntent.Reconcile ? intent : RegistrationOperationIntent.Deploy;

    private static void ValidateUnknownFields(Dictionary<string, JsonElement>? fields, string scope,
        RegistrationOperationIntent intent)
    {
        if (intent == RegistrationOperationIntent.Reconcile && fields is { Count: > 0 })
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                $"Registration version 2.0 does not support unknown fields at {scope} scope ({fields.Count} field(s)). Remove unsupported fields before reconciliation.");
    }

    private static PpdsException InvalidStructure() => new(ErrorCodes.Validation.InvalidValue,
        "Registration configuration requires non-null assembly, type, step, image and reset collections and entries.");
}
