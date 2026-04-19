using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using PPDS.DocsGen.Common;

namespace PPDS.DocsGen.Cli;

/// <summary>
/// Reflects over a built CLI assembly to produce markdown reference docs for
/// its <c>System.CommandLine</c> command tree. Groups are discovered by
/// invoking every <c>public static Command Create()</c> method on a public
/// type whose name ends in <c>CommandGroup</c>; each returned <c>Command</c>
/// is the root of a group whose direct subcommands become per-command
/// markdown files.
/// </summary>
/// <remarks>
/// Unlike <c>mcp-reflect</c>, this generator uses a full
/// <see cref="AssemblyLoadContext"/> rather than <see cref="MetadataLoadContext"/>
/// because System.CommandLine Command descriptions live on the runtime
/// instance — they are set either in the 2-arg constructor or via an object
/// initializer, so obtaining them requires invoking the factory method. The
/// factory contract is "construct and return a Command; no side effects," and
/// all target code in this repository adheres to it. Access to the resulting
/// Command tree is done via <see cref="PropertyInfo.GetValue"/> rather than
/// typed casts, so the generator is agnostic to which
/// <c>System.CommandLine</c> version the target assembly bound against.
/// </remarks>
public sealed class CliReferenceGenerator : IReferenceGenerator
{
    internal const string ToolName = "cli-reflect";

    private const string CommandTypeFullName = "System.CommandLine.Command";
    private const string CommandGroupTypeSuffix = "CommandGroup";
    private const string FactoryMethodName = "Create";

    public Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!File.Exists(input.SourceAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Source assembly not found: {input.SourceAssemblyPath}",
                input.SourceAssemblyPath);
        }

        var diagnostics = new List<string>();
        var groups = DiscoverGroups(input.SourceAssemblyPath, diagnostics);
        var files = new List<GeneratedFile>();

        foreach (var group in groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            var leafCommands = group.Leaves
                .OrderBy(c => c.CommandName, StringComparer.Ordinal)
                .ToList();

            foreach (var cmd in leafCommands)
            {
                files.Add(new GeneratedFile(
                    RelativePath: $"{group.Name}/{cmd.CommandName}.md",
                    Contents: MarkdownRenderer.RenderCommand(cmd)));
            }

            files.Add(new GeneratedFile(
                RelativePath: $"{group.Name}/_index.md",
                Contents: MarkdownRenderer.RenderGroupIndex(group.Name, leafCommands)));
        }

        return Task.FromResult(new GenerationResult(files, diagnostics));
    }

    private static IReadOnlyList<DiscoveredGroup> DiscoverGroups(
        string assemblyPath, List<string> diagnostics)
    {
        var loader = new FactoryLoadContext(assemblyPath);
        Assembly assembly;
        try
        {
            assembly = loader.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
        catch (Exception ex)
        {
            diagnostics.Add($"load: {ex.Message}");
            return Array.Empty<DiscoveredGroup>();
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            foreach (var loaderEx in ex.LoaderExceptions)
            {
                if (loaderEx is not null) diagnostics.Add($"load: {loaderEx.Message}");
            }
        }

        var groups = new List<DiscoveredGroup>();
        foreach (var type in types)
        {
            if (!type.IsClass || !type.IsPublic) continue;
            if (!type.Name.EndsWith(CommandGroupTypeSuffix, StringComparison.Ordinal)) continue;
            if (IsEditorBrowsableNever(type)) continue;

            var factory = type.GetMethod(
                FactoryMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (factory is null) continue;
            if (!IsCommandType(factory.ReturnType)) continue;
            if (IsEditorBrowsableNever(factory)) continue;

            object? root;
            try
            {
                root = factory.Invoke(null, parameters: null);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"factory {type.FullName}.Create() threw: {ex.InnerException?.Message ?? ex.Message}");
                continue;
            }

            if (root is null) continue;

            var groupName = GetString(root, "Name") ?? DeriveGroupNameFromType(type);
            var leaves = new List<DiscoveredCommand>();
            foreach (var subcommand in EnumerateChildren(root, "Subcommands"))
            {
                if (IsHidden(subcommand)) continue;
                leaves.Add(BuildCommandRecord(groupName, subcommand));
            }

            groups.Add(new DiscoveredGroup(groupName, leaves));
        }

        return groups;
    }

    private static DiscoveredCommand BuildCommandRecord(string groupName, object command)
    {
        var name = GetString(command, "Name") ?? string.Empty;
        var description = GetString(command, "Description") ?? string.Empty;

        var arguments = new List<CommandArgument>();
        foreach (var arg in EnumerateChildren(command, "Arguments"))
        {
            if (IsHidden(arg)) continue;
            arguments.Add(new CommandArgument(
                Name: GetString(arg, "Name") ?? string.Empty,
                Required: !HasDefaultValueFactory(arg),
                Description: GetString(arg, "Description") ?? string.Empty,
                TypeDisplay: DescribeValueType(arg.GetType(), openGenericBase: "Argument")));
        }

        var options = new List<CommandOption>();
        foreach (var opt in EnumerateChildren(command, "Options"))
        {
            if (IsHidden(opt)) continue;
            var (longName, shortName) = SplitOptionAliases(opt);
            options.Add(new CommandOption(
                LongName: longName,
                ShortName: shortName,
                TypeDisplay: DescribeValueType(opt.GetType(), openGenericBase: "Option"),
                DefaultValue: ReadDefaultValue(opt),
                Description: GetString(opt, "Description") ?? string.Empty));
        }

        return new DiscoveredCommand(
            GroupName: groupName,
            CommandName: name,
            Description: description,
            Arguments: arguments,
            Options: options);
    }

    private static (string LongName, string? ShortName) SplitOptionAliases(object option)
    {
        var name = GetString(option, "Name") ?? string.Empty;
        var aliases = EnumerateStringCollection(option, "Aliases").ToList();

        // By System.CommandLine convention, Name is the long form ("--foo").
        // Aliases contains short forms like "-f". Pick the first alias that
        // looks like a short form (single-dash), for stability.
        string? shortName = aliases
            .Where(a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal))
            .OrderBy(a => a, StringComparer.Ordinal)
            .FirstOrDefault();

        return (name, shortName);
    }

    private static string ReadDefaultValue(object option)
    {
        // Only surface explicit constant defaults (from a DefaultValue property,
        // when the option type exposes one). DefaultValueFactory-based defaults
        // are intentionally rendered as empty — the factory requires a parse
        // context to invoke, and the (factory) placeholder leaks an
        // implementation detail into user-facing reference docs. Most PPDS
        // options surface their defaults in the Description prose.
        var defaultValueProp = option.GetType().GetProperty("DefaultValue");
        if (defaultValueProp is not null)
        {
            var raw = defaultValueProp.GetValue(option);
            return FormatDefault(raw);
        }

        return string.Empty;
    }

    private static string FormatDefault(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsCommandType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == CommandTypeFullName) return true;
        }
        return false;
    }

    private static bool IsEditorBrowsableNever(MemberInfo member)
    {
        foreach (var attr in member.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName != typeof(EditorBrowsableAttribute).FullName) continue;
            if (attr.ConstructorArguments.Count == 0) continue;
            var raw = attr.ConstructorArguments[0].Value;
            if (raw is int i && i == (int)EditorBrowsableState.Never) return true;
        }
        return false;
    }

    private static string DeriveGroupNameFromType(Type type)
    {
        var name = type.Name;
        if (name.EndsWith(CommandGroupTypeSuffix, StringComparison.Ordinal))
            name = name[..^CommandGroupTypeSuffix.Length];
        return name.ToLowerInvariant();
    }

    private static bool IsHidden(object obj)
    {
        var prop = obj.GetType().GetProperty("Hidden", BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(obj) is bool b && b;
    }

    private static string? GetString(object obj, string propName)
    {
        var prop = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(obj) as string;
    }

    private static bool HasDefaultValueFactory(object obj)
    {
        var prop = obj.GetType().GetProperty("DefaultValueFactory", BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(obj) is not null;
    }

    private static IEnumerable<object> EnumerateChildren(object owner, string propName)
    {
        var prop = owner.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(owner) is not IEnumerable items) yield break;
        foreach (var item in items)
        {
            if (item is not null) yield return item;
        }
    }

    private static IEnumerable<string> EnumerateStringCollection(object owner, string propName)
    {
        foreach (var item in EnumerateChildren(owner, propName))
        {
            if (item is string s) yield return s;
        }
    }

    /// <summary>
    /// Renders <c>Option&lt;T&gt;</c> as the short form of <c>T</c>, falling
    /// back to a neutral token if the type is non-generic.
    /// </summary>
    private static string DescribeValueType(Type symbolType, string openGenericBase)
    {
        // Walk up until we find the generic Option`1 / Argument`1 form.
        for (var current = symbolType; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            var def = current.GetGenericTypeDefinition();
            if (def.Name != openGenericBase + "`1") continue;
            var arg = current.GenericTypeArguments[0];
            return TypeDisplay(arg);
        }

        return openGenericBase.ToLowerInvariant();
    }

    private static string TypeDisplay(Type t)
    {
        if (t.IsGenericType && t.Name == "Nullable`1" && t.GenericTypeArguments.Length == 1)
        {
            return TypeDisplay(t.GenericTypeArguments[0]) + "?";
        }

        if (t.IsGenericType)
        {
            var defName = t.Name;
            var tick = defName.IndexOf('`');
            var baseName = tick >= 0 ? defName[..tick] : defName;
            var args = string.Join(", ", t.GenericTypeArguments.Select(TypeDisplay));
            return $"{baseName}<{args}>";
        }

        return t.FullName switch
        {
            "System.String" => "string",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Boolean" => "bool",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Decimal" => "decimal",
            "System.Object" => "object",
            _ => t.Name,
        };
    }

    private sealed class FactoryLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _targetDir;

        public FactoryLoadContext(string targetAssemblyPath)
            : base(isCollectible: false)
        {
            var full = Path.GetFullPath(targetAssemblyPath);
            _resolver = new AssemblyDependencyResolver(full);
            _targetDir = Path.GetDirectoryName(full)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Prefer dependency manifest (.deps.json) resolution first.
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null && File.Exists(resolved))
                return LoadFromAssemblyPath(resolved);

            // Fallback: scan the target directory for a matching file.
            var candidate = Path.Combine(_targetDir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
                return LoadFromAssemblyPath(candidate);

            // Delegate to the default context for BCL / runtime assemblies.
            return null;
        }
    }

    internal sealed record DiscoveredGroup(string Name, IReadOnlyList<DiscoveredCommand> Leaves);

    internal sealed record DiscoveredCommand(
        string GroupName,
        string CommandName,
        string Description,
        IReadOnlyList<CommandArgument> Arguments,
        IReadOnlyList<CommandOption> Options);

    internal sealed record CommandArgument(string Name, bool Required, string Description, string TypeDisplay);

    internal sealed record CommandOption(string LongName, string? ShortName, string TypeDisplay, string DefaultValue, string Description);
}
