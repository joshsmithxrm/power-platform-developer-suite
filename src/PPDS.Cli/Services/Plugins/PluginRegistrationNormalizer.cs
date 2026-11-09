using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Shared nonsecret managed-field plan for registration writes and comparison.</summary>
internal static class PluginRegistrationNormalizer
{
    private static readonly HashSet<string> StepResets = new(StringComparer.Ordinal)
    {
        "filteringAttributes", "unsecureConfiguration", "description", "runAsUser",
        "asyncAutoDelete", "canBeBypassed", "canUseReadOnlyConnection"
    };
    private static readonly HashSet<string> ImageResets = new(StringComparer.Ordinal) { "attributes", "description" };

    internal static IReadOnlyDictionary<string, object?> NormalizeStep(PluginStepConfig config, RegistrationOperationIntent intent)
    {
        RejectUnknownFields(config.ExtensionData?.Count ?? 0, "step", intent);
        ValidateResets(config, StepResets);
        if (config.Images == null) throw Invalid("images must be an array; use an empty array to configure no images.");
        if (config.Name != null && string.IsNullOrWhiteSpace(config.Name)) throw Invalid("A supplied step name must not be empty.");
        if (config.ExecutionOrder is < PluginStepConfig.MinExecutionOrder or > PluginStepConfig.MaxExecutionOrder)
            throw Invalid("executionOrder must be between 1 and 999999.");
        if (config.IsSpecified("stage") && string.IsNullOrWhiteSpace(config.Stage)) throw Invalid("A supplied stage must not be null or empty.");
        if (config.IsSpecified("mode") && string.IsNullOrWhiteSpace(config.Mode)) throw Invalid("A supplied mode must not be null or empty.");
        var mode = NormalizeMode(!IsFull(intent) && !config.IsSpecified("mode") ? null : config.Mode);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = config.Name, ["message"] = config.Message,
            ["entity"] = NormalizeEntity(config.Entity), ["secondaryEntity"] = NormalizeEntity(config.SecondaryEntity),
            ["stage"] = NormalizeStage(!config.IsSpecified("stage") && string.IsNullOrEmpty(config.Stage) ? null : config.Stage)
        };
        if (IsFull(intent) || config.IsSpecified("mode")) fields["mode"] = mode;
        if (IsFull(intent) || config.IsSpecified("executionOrder")) fields["executionOrder"] = config.ExecutionOrder;
        if (IsFull(intent) || config.IsSpecified("enabled")) fields["enabled"] = config.Enabled;
        if (IsFull(intent) || config.IsSpecified("deployment"))
            fields["deployment"] = NormalizeDeployment(config.Deployment);
        if (IsFull(intent) || config.IsSpecified("invocationSource"))
            fields["invocationSource"] = NormalizeInvocationSource(config.InvocationSource);
        AddText(fields, config, intent, "filteringAttributes", config.FilteringAttributes, attributes: true);
        AddText(fields, config, intent, "unsecureConfiguration", config.UnsecureConfiguration);
        AddText(fields, config, intent, "description", config.Description);
        if (Manage(config, intent, "runAsUser", config.RunAsUser))
            fields["runAsUser"] = NormalizeRunAsUser(config.ResetFields.Contains("runAsUser") ? null : config.RunAsUser);
        AddBoolean(fields, config, intent, "canBeBypassed", config.CanBeBypassed, true);
        AddBoolean(fields, config, intent, "canUseReadOnlyConnection", config.CanUseReadOnlyConnection, false);
        AddBoolean(fields, config, intent, "asyncAutoDelete", config.AsyncAutoDelete, false);
        if (fields.TryGetValue("asyncAutoDelete", out var autoDelete) && fields.ContainsKey("mode") && mode != "Asynchronous")
        {
            if (autoDelete is true) throw Invalid("asyncAutoDelete=true requires Asynchronous mode.");
            fields.Remove("asyncAutoDelete"); // false/default is not applicable to synchronous steps
        }
        if (fields.TryGetValue("filteringAttributes", out var attributes) && !string.IsNullOrWhiteSpace(attributes as string) &&
            !string.Equals(config.Message, "Update", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Message, "UpdateMultiple", StringComparison.OrdinalIgnoreCase))
            throw Invalid("filteringAttributes requires Update or UpdateMultiple.");
        return fields;
    }

    internal static IReadOnlyDictionary<string, object?> NormalizeImage(PluginImageConfig config, string message, RegistrationOperationIntent intent)
    {
        RejectUnknownFields(config.ExtensionData?.Count ?? 0, "image", intent);
        ValidateResets(config, ImageResets);
        if (string.IsNullOrWhiteSpace(config.Name)) throw Invalid("Image name is required.");
        var messageProperty = PluginRegistrationService.GetDefaultImagePropertyName(message)
            ?? throw new PpdsException(ErrorCodes.Plugin.ImageNotSupported, $"Message '{message}' does not support plugin images.");
        if ((IsFull(intent) || config.IsSpecified("entityAlias")) && config.EntityAlias != null && string.IsNullOrWhiteSpace(config.EntityAlias)) throw Invalid("Image entityAlias must not be empty.");
        if ((IsFull(intent) || config.IsSpecified("messagePropertyName")) && config.MessagePropertyName != null && string.IsNullOrWhiteSpace(config.MessagePropertyName)) throw Invalid("Image messagePropertyName must not be empty.");
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = config.Name, ["imageType"] = NormalizeImageType(config.ImageType),
            ["entityAlias"] = config.EntityAlias ?? config.Name,
            ["messagePropertyName"] = config.MessagePropertyName ?? messageProperty
        };
        // Legacy partial image calls cannot author a message-property override. Preserve it on update.
        if (!IsFull(intent) && !config.IsSpecified("messagePropertyName")) fields.Remove("messagePropertyName");
        if (!IsFull(intent) && !config.IsSpecified("entityAlias")) fields.Remove("entityAlias");
        AddText(fields, config, intent, "attributes", config.Attributes, attributes: true);
        AddText(fields, config, intent, "description", config.Description);
        return fields;
    }

    internal static IReadOnlyDictionary<string, object?> NormalizeImagePatch(PluginImageConfig config, RegistrationOperationIntent intent = RegistrationOperationIntent.Patch)
    {
        ValidateResets(config, ImageResets);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddText(fields, config, intent, "attributes", config.Attributes, attributes: true);
        AddText(fields, config, intent, "description", config.Description);
        if (config.IsSpecified("entityAlias") && config.EntityAlias != null)
        {
            if (string.IsNullOrWhiteSpace(config.EntityAlias)) throw Invalid("Image entityAlias must not be empty.");
            fields["entityAlias"] = config.EntityAlias;
        }
        if (config.IsSpecified("messagePropertyName") && config.MessagePropertyName != null)
        {
            if (string.IsNullOrWhiteSpace(config.MessagePropertyName)) throw Invalid("Image messagePropertyName must not be empty.");
            fields["messagePropertyName"] = config.MessagePropertyName;
        }
        return fields;
    }

    private static void AddText(Dictionary<string, object?> fields, PluginConfigurationPresence config, RegistrationOperationIntent intent, string key, string? value, bool attributes = false)
    {
        if (!Manage(config, intent, key, value)) return;
        fields[key] = config.ResetFields.Contains(key) ? null
            : intent == RegistrationOperationIntent.Update && value == string.Empty ? string.Empty
            : attributes ? NormalizeAttributes(value) : string.IsNullOrEmpty(value) ? null : value;
    }

    private static void AddBoolean(Dictionary<string, object?> fields, PluginConfigurationPresence config, RegistrationOperationIntent intent, string key, bool? value, bool defaultValue)
    {
        if (config.ResetFields.Contains(key) || intent == RegistrationOperationIntent.Reconcile || config.IsSpecified(key) && value.HasValue)
            fields[key] = value ?? defaultValue;
    }

    private static bool Manage(PluginConfigurationPresence config, RegistrationOperationIntent intent, string key, string? value) =>
        config.ResetFields.Contains(key) || intent == RegistrationOperationIntent.Reconcile || config.IsSpecified(key) && (intent == RegistrationOperationIntent.Update ? value != null : !string.IsNullOrEmpty(value));

    private static bool IsFull(RegistrationOperationIntent intent) => intent is RegistrationOperationIntent.Deploy or RegistrationOperationIntent.Reconcile;

    private static void RejectUnknownFields(int count, string scope, RegistrationOperationIntent intent)
    {
        if (intent == RegistrationOperationIntent.Reconcile && count > 0)
            throw Invalid($"Registration version 2.0 does not support unknown fields at {scope} scope ({count} field(s)). Remove unsupported fields before reconciliation.");
    }

    private static void ValidateResets(PluginConfigurationPresence config, HashSet<string> allowed)
    {
        if (config.ResetFields == null) throw Invalid("resetFields must be an array.");
        foreach (var key in config.ResetFields)
        {
            if (key == null || !allowed.Contains(key)) throw Invalid("resetFields contains an unsupported field; secureConfiguration cannot be reset.");
            if (config.IsSpecified(key)) throw Invalid($"'{key}' cannot specify both a value and resetFields intent.");
        }
    }

    internal static string? NormalizeAttributes(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes)) return null;
        var names = attributes.Split(',').Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? null : string.Join(',', names);
    }

    internal static string? NormalizeRunAsUser(string? selector)
    {
        var normalized = selector?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Equals("CallingUser", StringComparison.OrdinalIgnoreCase)) return null;
        if (normalized.Equals("System", StringComparison.OrdinalIgnoreCase)) throw Invalid("runAsUser 'System' is unsupported; use CallingUser or an explicit systemuser GUID.");
        return normalized;
    }

    private static string NormalizeEntity(string? entity) => string.IsNullOrWhiteSpace(entity) ? "none" : entity.Trim().ToLowerInvariant();
    internal static string NormalizeStage(string? value) => EnumValue(value, "stage", "PostOperation", ("PreValidation", "10"), ("PreOperation", "20"), ("MainOperation", "30"), ("PostOperation", "40"));
    internal static string NormalizeMode(string? value) => EnumValue(value, "mode", "Synchronous", ("Synchronous", "0", "sync"), ("Asynchronous", "1", "async"));
    internal static string NormalizeDeployment(string? value) => EnumValue(value, "deployment", "ServerOnly", ("ServerOnly", "0"), ("Offline", "1"), ("Both", "2"));
    internal static string NormalizeInvocationSource(string? value) => EnumValue(value, "invocationSource", "Parent", ("Parent", "0"), ("Child", "1"), ("Internal", "-1"));
    internal static string NormalizeImageType(string? value) => EnumValue(value, "imageType", null, ("PreImage", "0", "pre"), ("PostImage", "1", "post"), ("Both", "2", null));

    private static string EnumValue(string? value, string field, string? defaultValue, params (string Name, string Number, string? Alias)[] values)
    {
        if (value == null && defaultValue != null) return defaultValue;
        var token = value?.Trim();
        foreach (var entry in values)
            if (string.Equals(token, entry.Name, StringComparison.OrdinalIgnoreCase) || token == entry.Number || entry.Alias != null && string.Equals(token, entry.Alias, StringComparison.OrdinalIgnoreCase)) return entry.Name;
        throw Invalid($"Invalid {field}; supply a supported named or numeric value.");
    }
    private static string EnumValue(string? value, string field, string? defaultValue, params (string Name, string Number)[] values) =>
        EnumValue(value, field, defaultValue, values.Select(v =>
        {
            (string Name, string Number, string? Alias) entry = (v.Name, v.Number, null);
            return entry;
        }).ToArray());
    private static PpdsException Invalid(string message) => new(ErrorCodes.Validation.InvalidValue, message);
}
