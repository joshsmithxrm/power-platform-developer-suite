using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PPDS.Cli.Plugins.Models;
using PPDS.Plugins;

namespace PPDS.Cli.Plugins.Extraction;

/// <summary>
/// Extracts plugin registration information from assemblies using MetadataLoadContext.
/// </summary>
public sealed class AssemblyExtractor : IDisposable
{
    private readonly MetadataLoadContext _metadataLoadContext;
    private readonly string _assemblyPath;
    private bool _disposed;

    private AssemblyExtractor(MetadataLoadContext metadataLoadContext, string assemblyPath)
    {
        _metadataLoadContext = metadataLoadContext;
        _assemblyPath = assemblyPath;
    }

    /// <summary>
    /// Creates an extractor for the specified assembly.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly DLL.</param>
    /// <returns>An extractor instance that must be disposed.</returns>
    public static AssemblyExtractor Create(string assemblyPath)
    {
        var directory = Path.GetDirectoryName(assemblyPath) ?? ".";

        // Collect assemblies for the resolver:
        // 1. All DLLs in the same directory as the target assembly
        // 2. .NET runtime assemblies for core types
        var assemblyPaths = new List<string>();

        // Add assemblies from target directory
        assemblyPaths.AddRange(Directory.GetFiles(directory, "*.dll"));

        // Add .NET runtime assemblies
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        assemblyPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

        var resolver = new PathAssemblyResolver(assemblyPaths);
        var mlc = new MetadataLoadContext(resolver);

        return new AssemblyExtractor(mlc, assemblyPath);
    }

    /// <summary>
    /// Extracts plugin registration configuration from the assembly.
    /// </summary>
    /// <returns>Assembly configuration with all plugin types and steps.</returns>
    public PluginAssemblyConfig Extract()
    {
        var assembly = _metadataLoadContext.LoadFromAssemblyPath(_assemblyPath);
        var assemblyName = assembly.GetName();

        var config = new PluginAssemblyConfig
        {
            Name = assemblyName.Name ?? Path.GetFileNameWithoutExtension(_assemblyPath),
            Type = "Assembly",
            Path = Path.GetFileName(_assemblyPath),
            AllTypeNames = [],
            Types = []
        };

        var customApis = new List<CustomApiConfig>();

        // Get all exported types (public, non-abstract, non-interface)
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            // Extract Custom API if annotated
            var customApiAttr = GetCustomApiAttribute(type);
            if (customApiAttr != null)
            {
                var parameterAttributes = GetCustomApiParameterAttributes(type);
                var apiConfig = MapCustomApiAttribute(customApiAttr, type, parameterAttributes);
                customApis.Add(apiConfig);
            }

            // Check if type has plugin step attributes
            var stepAttributes = GetPluginStepAttributes(type);
            if (stepAttributes.Count == 0)
                continue;

            // Track all plugin type names for orphan detection
            config.AllTypeNames.Add(type.FullName ?? type.Name);

            var pluginType = new PluginTypeConfig
            {
                TypeName = type.FullName ?? type.Name,
                Steps = []
            };

            var imageAttributes = GetPluginImageAttributes(type);

            foreach (var stepAttr in stepAttributes)
            {
                var step = MapStepAttribute(stepAttr, type);

                // Find images for this step
                var stepImages = imageAttributes
                    .Where(img => MatchesStep(img, stepAttr))
                    .Select(MapImageAttribute)
                    .ToList();

                step.Images = stepImages;
                pluginType.Steps.Add(step);
            }

            config.Types.Add(pluginType);
        }

        if (customApis.Count > 0)
            config.CustomApis = customApis;

        return config;
    }

    private List<CustomAttributeData> GetPluginStepAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(PluginStepAttribute).FullName)
            .ToList();
    }

    private List<CustomAttributeData> GetPluginImageAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(PluginImageAttribute).FullName)
            .ToList();
    }

    private CustomAttributeData? GetCustomApiAttribute(Type type)
    {
        return type.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == typeof(CustomApiAttribute).FullName);
    }

    private List<CustomAttributeData> GetCustomApiParameterAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(CustomApiParameterAttribute).FullName)
            .ToList();
    }

    private static CustomApiConfig MapCustomApiAttribute(
        CustomAttributeData attr,
        Type pluginType,
        List<CustomAttributeData> parameterAttrs)
    {
        var api = new CustomApiConfig
        {
            PluginTypeName = pluginType.FullName ?? pluginType.Name
        };

        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "UniqueName":
                    api.UniqueName = value?.ToString() ?? string.Empty;
                    break;
                case "DisplayName":
                    api.DisplayName = value?.ToString() ?? string.Empty;
                    break;
                case "Name":
                    api.Name = value?.ToString();
                    break;
                case "Description":
                    api.Description = value?.ToString();
                    break;
                case "BindingType":
                    var bindingStr = MapBindingTypeValue(value);
                    // Only write non-default (Global = 0)
                    if (bindingStr != "Global")
                        api.BindingType = bindingStr;
                    break;
                case "BoundEntity":
                    api.BoundEntity = value?.ToString();
                    break;
                case "IsFunction":
                    api.IsFunction = value is true;
                    break;
                case "IsPrivate":
                    api.IsPrivate = value is true;
                    break;
                case "ExecutePrivilegeName":
                    api.ExecutePrivilegeName = value?.ToString();
                    break;
                case "AllowedProcessingStepType":
                    var stepTypeStr = MapProcessingStepTypeValue(value);
                    // Only write non-default (None = 0)
                    if (stepTypeStr != "None")
                        api.AllowedProcessingStepType = stepTypeStr;
                    break;
            }
        }

        if (parameterAttrs.Count > 0)
        {
            api.Parameters = parameterAttrs
                .Select(MapCustomApiParameterAttribute)
                .ToList();
        }

        return api;
    }

    private static CustomApiParameterConfig MapCustomApiParameterAttribute(CustomAttributeData attr)
    {
        var param = new CustomApiParameterConfig();

        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "Name":
                    param.Name = value?.ToString() ?? string.Empty;
                    break;
                case "UniqueName":
                    param.UniqueName = value?.ToString();
                    break;
                case "DisplayName":
                    param.DisplayName = value?.ToString();
                    break;
                case "Description":
                    param.Description = value?.ToString();
                    break;
                case "Type":
                    param.Type = MapParameterTypeValue(value);
                    break;
                case "LogicalEntityName":
                    param.LogicalEntityName = value?.ToString();
                    break;
                case "IsOptional":
                    param.IsOptional = value is true;
                    break;
                case "Direction":
                    param.Direction = MapParameterDirectionValue(value);
                    break;
            }
        }

        return param;
    }

    private static string MapBindingTypeValue(object? value)
    {
        // ApiBindingType enum: Global=0, Entity=1, EntityCollection=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Global",
                1 => "Entity",
                2 => "EntityCollection",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Global";
    }

    private static string MapProcessingStepTypeValue(object? value)
    {
        // ApiProcessingStepType enum: None=0, AsyncOnly=1, SyncAndAsync=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "None",
                1 => "AsyncOnly",
                2 => "SyncAndAsync",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "None";
    }

    private static string MapParameterTypeValue(object? value)
    {
        // ApiParameterType enum: Boolean=0, DateTime=1, Decimal=2, Entity=3,
        // EntityCollection=4, EntityReference=5, Float=6, Integer=7, Money=8,
        // Picklist=9, String=10, StringArray=11, Guid=12
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Boolean",
                1 => "DateTime",
                2 => "Decimal",
                3 => "Entity",
                4 => "EntityCollection",
                5 => "EntityReference",
                6 => "Float",
                7 => "Integer",
                8 => "Money",
                9 => "Picklist",
                10 => "String",
                11 => "StringArray",
                12 => "Guid",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "String";
    }

    private static string MapParameterDirectionValue(object? value)
    {
        // ParameterDirection enum: Input=0, Output=1
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Input",
                1 => "Output",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Input";
    }

    private static PluginStepConfig MapStepAttribute(CustomAttributeData attr, Type pluginType)
    {
        var step = new PluginStepConfig();

        // Handle constructor arguments
        var ctorParams = attr.Constructor.GetParameters();
        for (var i = 0; i < attr.ConstructorArguments.Count; i++)
        {
            var paramName = ctorParams[i].Name;
            var value = attr.ConstructorArguments[i].Value;

            switch (paramName)
            {
                case "message":
                    step.Message = value?.ToString() ?? string.Empty;
                    break;
                case "entityLogicalName":
                    step.Entity = value?.ToString() ?? string.Empty;
                    break;
                case "stage":
                    step.Stage = MapStageValue(value);
                    break;
            }
        }

        // Handle named arguments
        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "Message":
                    step.Message = value?.ToString() ?? string.Empty;
                    break;
                case "EntityLogicalName":
                    step.Entity = value?.ToString() ?? string.Empty;
                    break;
                case "SecondaryEntityLogicalName":
                    step.SecondaryEntity = value?.ToString();
                    break;
                case "Stage":
                    step.Stage = MapStageValue(value);
                    break;
                case "Mode":
                    step.Mode = MapModeValue(value);
                    break;
                case "FilteringAttributes":
                    step.FilteringAttributes = value?.ToString();
                    break;
                case "ExecutionOrder":
                    step.ExecutionOrder = value is int order ? order : 1;
                    break;
                case "Name":
                    step.Name = value?.ToString();
                    break;
                case "UnsecureConfiguration":
                    step.UnsecureConfiguration = value?.ToString();
                    break;
                case "Description":
                    step.Description = value?.ToString();
                    break;
                case "AsyncAutoDelete":
                    step.AsyncAutoDelete = value is true;
                    break;
                case "StepId":
                    step.StepId = value?.ToString();
                    break;
                case "Deployment":
                    // Only write non-default value (default is ServerOnly = 0)
                    var deploymentStr = MapDeploymentValue(value);
                    if (deploymentStr != "ServerOnly")
                        step.Deployment = deploymentStr;
                    break;
                case "RunAsUser":
                    step.RunAsUser = value?.ToString();
                    break;
                case "CanBeBypassed":
                    // Default is true — only write when false (non-default)
                    if (value is false)
                        step.CanBeBypassed = false;
                    break;
                case "CanUseReadOnlyConnection":
                    // Default is false — only write when true (non-default)
                    if (value is true)
                        step.CanUseReadOnlyConnection = true;
                    break;
                case "InvocationSource":
                    // Default is Parent = 0 — only write when Child (non-default)
                    var invocationStr = MapInvocationSourceValue(value);
                    if (invocationStr != "Parent")
                        step.InvocationSource = invocationStr;
                    break;
            }
        }

        // Auto-generate name if not specified
        if (string.IsNullOrEmpty(step.Name))
        {
            var typeName = pluginType.Name;
            step.Name = $"{typeName}: {step.Message} of {step.Entity}";
        }

        return step;
    }

    private static PluginImageConfig MapImageAttribute(CustomAttributeData attr)
    {
        var image = new PluginImageConfig();

        // Handle constructor arguments
        var ctorParams = attr.Constructor.GetParameters();
        for (var i = 0; i < attr.ConstructorArguments.Count; i++)
        {
            var paramName = ctorParams[i].Name;
            var value = attr.ConstructorArguments[i].Value;

            switch (paramName)
            {
                case "imageType":
                    image.ImageType = MapImageTypeValue(value);
                    break;
                case "name":
                    image.Name = value?.ToString() ?? string.Empty;
                    break;
                case "attributes":
                    image.Attributes = value?.ToString();
                    break;
            }
        }

        // Handle named arguments
        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "ImageType":
                    image.ImageType = MapImageTypeValue(value);
                    break;
                case "Name":
                    image.Name = value?.ToString() ?? string.Empty;
                    break;
                case "Attributes":
                    image.Attributes = value?.ToString();
                    break;
                case "EntityAlias":
                    image.EntityAlias = value?.ToString();
                    break;
                case "Description":
                    image.Description = value?.ToString();
                    break;
                case "MessagePropertyName":
                    image.MessagePropertyName = value?.ToString();
                    break;
            }
        }

        // Default entityAlias to name if not explicitly specified
        image.EntityAlias ??= image.Name;

        return image;
    }

    private static bool MatchesStep(CustomAttributeData imageAttr, CustomAttributeData stepAttr)
    {
        // Get StepId from both attributes using LINQ for clearer intent
        var imageStepId = imageAttr.NamedArguments
            .FirstOrDefault(na => na.MemberName == "StepId")
            .TypedValue.Value?.ToString();

        var stepStepId = stepAttr.NamedArguments
            .FirstOrDefault(na => na.MemberName == "StepId")
            .TypedValue.Value?.ToString();

        // If image has no StepId, it applies to all steps (or the only step)
        if (string.IsNullOrEmpty(imageStepId))
            return true;

        // If image has StepId, it must match the step's StepId
        return string.Equals(imageStepId, stepStepId, StringComparison.Ordinal);
    }

    private static string MapStageValue(object? value)
    {
        // Handle enum by underlying value
        if (value is int intValue)
        {
            return intValue switch
            {
                10 => "PreValidation",
                20 => "PreOperation",
                30 => "MainOperation",
                40 => "PostOperation",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "PostOperation";
    }

    private static string MapModeValue(object? value)
    {
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Synchronous",
                1 => "Asynchronous",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Synchronous";
    }

    private static string MapImageTypeValue(object? value)
    {
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "PreImage",
                1 => "PostImage",
                2 => "Both",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "PreImage";
    }

    private static string MapDeploymentValue(object? value)
    {
        // PluginDeployment enum: ServerOnly=0, Offline=1, Both=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "ServerOnly",
                1 => "Offline",
                2 => "Both",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "ServerOnly";
    }

    private static string MapInvocationSourceValue(object? value)
    {
        // PluginInvocationSource enum: Parent=0, Child=1
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Parent",
                1 => "Child",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Parent";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _metadataLoadContext.Dispose();
        _disposed = true;
    }
}
