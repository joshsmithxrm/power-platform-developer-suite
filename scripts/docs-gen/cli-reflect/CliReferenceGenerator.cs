using System.Collections.Immutable;
using System.Reflection;
using PPDS.DocsGen.Common;

namespace PPDS.DocsGen.Cli;

/// <summary>
/// Reflects over a built <c>PPDS.Cli.dll</c> (or any assembly containing
/// Spectre command classes) using <see cref="MetadataLoadContext"/>, groups
/// commands by the last segment of their containing namespace, and emits
/// deterministic markdown reference docs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MetadataLoadContext"/> is used (mirroring <c>mcp-reflect</c>)
/// so the generator never executes any code from the target assembly — it
/// only reads metadata. This is robust against a target with broken
/// dependencies or side-effecting static state.
/// </para>
/// <para>
/// Limitation: <see cref="DiscoverDescription"/> supports the
/// <c>[Description]</c> attribute form only. Descriptions supplied via a
/// property initializer or a ctor-body assignment (the other two patterns
/// mentioned in <c>specs/docs-generation.md</c> §Surface CLI) are not
/// resolved here — reading those would require invoking code, which
/// <see cref="MetadataLoadContext"/> does not permit. The attribute form
/// covers 95% of the PPDS codebase; the <c>PPDS015</c> analyzer gates the
/// remaining cases upstream.
/// </para>
/// </remarks>
public sealed class CliReferenceGenerator : IReferenceGenerator
{
    internal const string ToolName = "cli-reflect";

    // Fully-qualified names of Spectre's base command types. Matching by
    // original (unconstructed) definition lets us catch Command&lt;TSettings&gt;
    // just like Command, and AsyncCommand&lt;TSettings&gt; just like AsyncCommand.
    private static readonly ImmutableHashSet<string> SpectreBaseTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Spectre.Console.Cli.Command",
        "Spectre.Console.Cli.AsyncCommand");

    private const string DescriptionAttributeName = "DescriptionAttribute";
    private const string EditorBrowsableAttributeName = "EditorBrowsableAttribute";
    private const string CommandArgumentAttributeName = "Spectre.Console.Cli.CommandArgumentAttribute";
    private const string CommandOptionAttributeName = "Spectre.Console.Cli.CommandOptionAttribute";
    private const string SpectreDefaultValueAttributeName = "Spectre.Console.Cli.DefaultValueAttribute";
    private const string BclDefaultValueAttributeName = "System.ComponentModel.DefaultValueAttribute";

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
        var commands = DiscoverCommands(input.SourceAssemblyPath, diagnostics);
        var files = new List<GeneratedFile>();

        var byGroup = commands
            .GroupBy(c => c.GroupName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byGroup)
        {
            var ordered = group.OrderBy(c => c.CommandName, StringComparer.Ordinal).ToList();

            foreach (var cmd in ordered)
            {
                files.Add(new GeneratedFile(
                    RelativePath: $"{group.Key}/{cmd.CommandName}.md",
                    Contents: MarkdownRenderer.RenderCommand(cmd)));
            }

            files.Add(new GeneratedFile(
                RelativePath: $"{group.Key}/_index.md",
                Contents: MarkdownRenderer.RenderGroupIndex(group.Key, ordered)));
        }

        return Task.FromResult(new GenerationResult(files, diagnostics));
    }

    /// <summary>
    /// Walks the assembly under a fresh <see cref="MetadataLoadContext"/> and
    /// returns every concrete Spectre command type, skipping abstract classes
    /// and any type decorated <c>[EditorBrowsable(Never)]</c>.
    /// </summary>
    private static IReadOnlyList<DiscoveredCommand> DiscoverCommands(
        string assemblyPath, List<string> diagnostics)
    {
        var resolver = new FallbackAssemblyResolver(BuildRuntimeAssemblies(assemblyPath));
        using var mlc = new MetadataLoadContext(resolver);
        var assembly = mlc.LoadFromAssemblyPath(assemblyPath);

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
                if (loaderEx is not null)
                    diagnostics.Add("load: " + loaderEx.Message);
            }
        }

        var results = new List<DiscoveredCommand>();
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            if (!InheritsFromSpectreCommand(type) || IsEditorBrowsableNever(type))
            {
                continue;
            }

            results.Add(BuildCommand(type));
        }

        return results;
    }

    private static bool InheritsFromSpectreCommand(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SpectreBaseTypes.Contains(NormalisedTypeName(current)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalisedTypeName(Type type)
    {
        // MetadataLoadContext can refuse GetGenericTypeDefinition() for certain
        // closed generics built from metadata; fall back to stripping the arity
        // suffix from the closed-form FullName.
        string fullName;
        try
        {
            var def = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            fullName = def.FullName ?? string.Empty;
        }
        catch (NotSupportedException)
        {
            fullName = type.FullName ?? string.Empty;
        }

        var tick = fullName.IndexOf('`');
        return tick >= 0 ? fullName.Substring(0, tick) : fullName;
    }

    private static bool IsEditorBrowsableNever(MemberInfo member)
    {
        // Attribute-data-only access under MetadataLoadContext (no runtime
        // instance construction). EditorBrowsableState.Never == 1.
        foreach (var attr in member.GetCustomAttributesData())
        {
            if (attr.AttributeType.Name != EditorBrowsableAttributeName)
            {
                continue;
            }

            if (attr.ConstructorArguments.Count == 0)
            {
                continue;
            }

            var raw = attr.ConstructorArguments[0].Value;
            if (raw is int i && i == 1) return true;
        }

        return false;
    }

    private static DiscoveredCommand BuildCommand(Type type)
    {
        var settingsType = ResolveSettingsType(type);
        var (args, opts) = settingsType is null
            ? (Array.Empty<CommandArgument>(), Array.Empty<CommandOption>())
            : ExtractArgsAndOptions(settingsType);

        return new DiscoveredCommand(
            Type: type,
            GroupName: DeriveGroupName(type),
            CommandName: DeriveCommandName(type),
            Description: DiscoverDescription(type),
            Arguments: args,
            Options: opts);
    }

    /// <summary>
    /// Group name is the last segment of the containing namespace
    /// (e.g. <c>Foo.Cli.Commands.Auth.LoginCommand</c> → <c>auth</c>).
    /// Namespaces ending in <c>Commands</c> (no sub-group) become <c>general</c>.
    /// </summary>
    private static string DeriveGroupName(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        var lastDot = ns.LastIndexOf('.');
        var last = lastDot < 0 ? ns : ns.Substring(lastDot + 1);

        if (string.IsNullOrEmpty(last) || string.Equals(last, "Commands", StringComparison.Ordinal))
        {
            return "general";
        }

        return last.ToLowerInvariant();
    }

    /// <summary>
    /// Command name = type name with a trailing <c>Command</c> suffix trimmed
    /// (if present), lower-cased.
    /// </summary>
    private static string DeriveCommandName(Type type)
    {
        var name = type.Name;
        const string suffix = "Command";
        if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
        {
            name = name.Substring(0, name.Length - suffix.Length);
        }

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Walks the base-type chain to the first generic Spectre command and
    /// returns its <c>TSettings</c> argument. Returns <c>null</c> for
    /// non-generic bases (e.g. direct subclasses of <c>Command</c>).
    /// </summary>
    private static Type? ResolveSettingsType(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GenericTypeArguments.Length == 1
                && SpectreBaseTypes.Contains(NormalisedTypeName(current)))
            {
                return current.GenericTypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Discovers the command description from <c>[Description]</c> on the
    /// type. Property-initializer and ctor-body assignments are not supported
    /// under <see cref="MetadataLoadContext"/> (no code execution); see the
    /// class remarks and the <c>PPDS015</c> analyzer which gates them
    /// upstream.
    /// </summary>
    private static string DiscoverDescription(Type type) =>
        ReadSystemComponentModelDescription(type.GetCustomAttributesData());

    private static string DiscoverPropertyDescription(PropertyInfo prop) =>
        ReadSystemComponentModelDescription(prop.GetCustomAttributesData());

    private static string ReadSystemComponentModelDescription(IList<CustomAttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            if (attr.AttributeType.Name != DescriptionAttributeName)
            {
                continue;
            }

            if (attr.AttributeType.Namespace != "System.ComponentModel")
            {
                continue;
            }

            if (attr.ConstructorArguments.Count > 0
                && attr.ConstructorArguments[0].Value is string s
                && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return string.Empty;
    }

    private static (CommandArgument[] Args, CommandOption[] Opts) ExtractArgsAndOptions(Type settings)
    {
        var argList = new List<CommandArgument>();
        var optList = new List<CommandOption>();

        // MetadataToken is the stable declaration order — reflection's
        // default property enumeration order is runtime-dependent.
        var props = settings
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.MetadataToken)
            .ToList();

        foreach (var prop in props)
        {
            if (IsEditorBrowsableNever(prop))
            {
                continue;
            }

            var argAttr = TryReadCommandArgument(prop);
            if (argAttr is not null)
            {
                argList.Add(new CommandArgument(
                    Name: argAttr.Name,
                    Required: argAttr.Required,
                    Description: DiscoverPropertyDescription(prop),
                    TypeDisplay: TypeDisplay(prop.PropertyType)));
                continue;
            }

            var optAttr = TryReadCommandOption(prop);
            if (optAttr is not null)
            {
                optList.Add(new CommandOption(
                    LongName: optAttr.LongName,
                    ShortName: optAttr.ShortName,
                    TypeDisplay: TypeDisplay(prop.PropertyType),
                    DefaultValue: ReadDefaultValue(prop),
                    Description: DiscoverPropertyDescription(prop)));
            }
        }

        return (argList.ToArray(), optList.ToArray());
    }

    private static string TypeDisplay(Type t)
    {
        // Unwrap Nullable<T> to T? for readability. Nullable.GetUnderlyingType
        // works across runtime contexts by checking FullName equality against
        // the known Nullable`1 definition; under MetadataLoadContext the Type
        // it returns is still in the same MLC, which is what we want.
        if (t.IsGenericType && t.Name == "Nullable`1" && t.GenericTypeArguments.Length == 1)
        {
            return TypeDisplay(t.GenericTypeArguments[0]) + "?";
        }

        if (t.IsGenericType)
        {
            // Avoid GetGenericTypeDefinition() — MetadataLoadContext rejects
            // it for some closed generics. Strip the arity suffix instead.
            var defName = t.Name;
            var tick = defName.IndexOf('`');
            var baseName = tick >= 0 ? defName.Substring(0, tick) : defName;
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

    /// <summary>
    /// Returns a parsed <see cref="ArgumentInfo"/> if the property carries
    /// <c>[CommandArgument]</c>. The Spectre template string (e.g.
    /// <c>&lt;name&gt;</c> for required, <c>[name]</c> for optional) is parsed
    /// to a deterministic name + required flag.
    /// </summary>
    private static ArgumentInfo? TryReadCommandArgument(PropertyInfo prop)
    {
        foreach (var attr in prop.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName != CommandArgumentAttributeName)
            {
                continue;
            }

            // Signature is [CommandArgument(int position, string template)].
            if (attr.ConstructorArguments.Count < 2)
            {
                continue;
            }

            var template = attr.ConstructorArguments[1].Value as string ?? string.Empty;
            return ParseArgumentTemplate(template);
        }

        return null;
    }

    /// <summary>
    /// Returns a parsed <see cref="OptionInfo"/> if the property carries
    /// <c>[CommandOption]</c>. Both short (<c>-n</c>) and long (<c>--name</c>)
    /// forms are extracted from the template; missing parts are null.
    /// </summary>
    private static OptionInfo? TryReadCommandOption(PropertyInfo prop)
    {
        foreach (var attr in prop.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName != CommandOptionAttributeName)
            {
                continue;
            }

            if (attr.ConstructorArguments.Count < 1)
            {
                continue;
            }

            var template = attr.ConstructorArguments[0].Value as string ?? string.Empty;
            return ParseOptionTemplate(template);
        }

        return null;
    }

    /// <summary>
    /// Reads <c>[DefaultValue]</c> from either <c>System.ComponentModel</c> or
    /// Spectre's own namespace, returning a stable string form (<c>true</c>,
    /// <c>42</c>, empty for null).
    /// </summary>
    private static string ReadDefaultValue(PropertyInfo prop)
    {
        foreach (var attr in prop.GetCustomAttributesData())
        {
            var fullName = attr.AttributeType.FullName;
            if (fullName != BclDefaultValueAttributeName
                && fullName != SpectreDefaultValueAttributeName)
            {
                continue;
            }

            if (attr.ConstructorArguments.Count == 0)
            {
                continue;
            }

            var raw = attr.ConstructorArguments[0].Value;
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

    /// <summary>
    /// Parses a Spectre argument template like <c>&lt;name&gt;</c> or
    /// <c>[name]</c>. Angle-bracketed = required; square-bracketed = optional;
    /// bare = required by convention.
    /// </summary>
    private static ArgumentInfo ParseArgumentTemplate(string template)
    {
        template = template.Trim();
        if (template.Length >= 2 && template[0] == '<' && template[^1] == '>')
        {
            return new ArgumentInfo(template[1..^1], Required: true);
        }

        if (template.Length >= 2 && template[0] == '[' && template[^1] == ']')
        {
            return new ArgumentInfo(template[1..^1], Required: false);
        }

        return new ArgumentInfo(template, Required: true);
    }

    /// <summary>
    /// Parses a Spectre option template like <c>-n|--name &lt;NAME&gt;</c>.
    /// </summary>
    private static OptionInfo ParseOptionTemplate(string template)
    {
        template = template.Trim();

        // Drop the value placeholder (anything after the first space/tab) — it
        // represents the argument name, which we don't surface here.
        var firstSpace = template.IndexOf(' ');
        if (firstSpace >= 0)
        {
            template = template.Substring(0, firstSpace);
        }

        string? shortName = null;
        string longName = string.Empty;
        foreach (var part in template.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                longName = trimmed;
            }
            else if (trimmed.StartsWith('-'))
            {
                shortName = trimmed;
            }
        }

        return new OptionInfo(longName, shortName);
    }

    private static string[] BuildRuntimeAssemblies(string targetAssemblyPath)
    {
        // MetadataLoadContext requires a resolver that can find every
        // referenced assembly, including the BCL. Use the current runtime
        // directory + the target's directory.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetAssemblyPath))!;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
            paths.Add(dll);
        if (Directory.Exists(targetDir))
        {
            foreach (var dll in Directory.GetFiles(targetDir, "*.dll"))
                paths.Add(dll);
        }
        paths.Add(Path.GetFullPath(targetAssemblyPath));
        return paths.ToArray();
    }

    /// <summary>
    /// Resolver that first consults a seed list of absolute paths, then
    /// falls back to scanning the user's NuGet global package cache when an
    /// attribute references a transitively-loaded assembly that wasn't
    /// copied to the target's bin dir. Mirrors the pattern in
    /// <c>mcp-reflect</c>'s <c>McpReferenceGenerator</c>.
    /// </summary>
    private sealed class FallbackAssemblyResolver : MetadataAssemblyResolver
    {
        private readonly Dictionary<string, string> _byName;
        private readonly List<string> _searchDirs;

        public FallbackAssemblyResolver(string[] seedPaths)
        {
            _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _searchDirs = new List<string>();
            foreach (var path in seedPaths)
            {
                var simple = Path.GetFileNameWithoutExtension(path);
                _byName.TryAdd(simple, path);
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && !_searchDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    _searchDirs.Add(dir);
            }

            var nugetHome = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            if (Directory.Exists(nugetHome))
                _searchDirs.Add(nugetHome);
        }

        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (string.IsNullOrEmpty(name)) return null;

            if (_byName.TryGetValue(name, out var cached))
                return context.LoadFromAssemblyPath(cached);

            foreach (var dir in _searchDirs)
            {
                var direct = Path.Combine(dir, name + ".dll");
                if (File.Exists(direct))
                {
                    _byName[name] = direct;
                    return context.LoadFromAssemblyPath(direct);
                }
            }

            var nugetHome = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            if (Directory.Exists(nugetHome))
            {
                try
                {
                    var matches = Directory.EnumerateFiles(
                        nugetHome, name + ".dll", SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    string? best = matches.FirstOrDefault(p => p.Contains("net8.0", StringComparison.Ordinal))
                        ?? matches.FirstOrDefault(p => p.Contains("net9.0", StringComparison.Ordinal))
                        ?? matches.FirstOrDefault(p => p.Contains("netstandard2.0", StringComparison.Ordinal))
                        ?? matches.FirstOrDefault();
                    if (best is not null)
                    {
                        _byName[name] = best;
                        return context.LoadFromAssemblyPath(best);
                    }
                }
                catch
                {
                    // Swallow; return null and the caller decides.
                }
            }

            return null;
        }
    }

    private sealed record ArgumentInfo(string Name, bool Required);

    private sealed record OptionInfo(string LongName, string? ShortName);

    internal sealed record DiscoveredCommand(
        Type Type,
        string GroupName,
        string CommandName,
        string Description,
        IReadOnlyList<CommandArgument> Arguments,
        IReadOnlyList<CommandOption> Options);

    internal sealed record CommandArgument(string Name, bool Required, string Description, string TypeDisplay);

    internal sealed record CommandOption(string LongName, string? ShortName, string TypeDisplay, string DefaultValue, string Description);
}
