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

        // Get all exported types (public, non-abstract, non-interface)
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            // Check if type implements IPlugin or has plugin attributes
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
                    step.Configuration = value?.ToString();
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
            }
        }

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

    public void Dispose()
    {
        if (_disposed)
            return;

        _metadataLoadContext.Dispose();
        _disposed = true;
    }
}
