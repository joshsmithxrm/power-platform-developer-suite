using System.Reflection;
using System.Text;
using PPDS.DocsGen.Common;

namespace PPDS.DocsGen.Mcp;

/// <summary>
/// Reflects over an <c>PPDS.Mcp.dll</c>-style assembly via
/// <see cref="MetadataLoadContext"/>, enumerates every method carrying a
/// <c>[McpServerTool]</c> attribute, and emits one markdown page per tool plus
/// a grouped index. Implements the shared <see cref="IReferenceGenerator"/>
/// contract (AC-17, AC-19).
/// </summary>
public sealed class McpReferenceGenerator : IReferenceGenerator
{
    private const string ToolName = "mcp-reflect";
    private const string McpServerToolAttributeName = "McpServerToolAttribute";
    private const string DescriptionAttributeName = "DescriptionAttribute";
    private const string McpToolExampleAttributeName = "McpToolExampleAttribute";

    /// <inheritdoc />
    public Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct)
    {
        if (!File.Exists(input.SourceAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Assembly not found: {input.SourceAssemblyPath}", input.SourceAssemblyPath);
        }

        var diagnostics = new List<string>();
        var tools = LoadTools(input.SourceAssemblyPath, diagnostics);

        // Determinism (AC-19): sort tools by name.
        tools.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var files = new List<GeneratedFile>();
        foreach (var tool in tools)
        {
            files.Add(new GeneratedFile(
                $"tools/{tool.Name}.md",
                RenderTool(tool)));
        }

        files.Add(new GeneratedFile("_index.md", RenderIndex(tools)));

        return Task.FromResult(new GenerationResult(files, diagnostics));
    }

    private static List<ToolInfo> LoadTools(string assemblyPath, List<string> diagnostics)
    {
        var tools = new List<ToolInfo>();
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
            // Some types may fail to load if an obscure reference is missing,
            // but the loaded set is still enumerable. Surface the rest as
            // diagnostics (Constitution I1: stderr-worthy).
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            foreach (var loaderEx in ex.LoaderExceptions)
            {
                if (loaderEx is not null)
                    diagnostics.Add("load: " + loaderEx.Message);
            }
        }

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var mcpAttr = FindAttribute(method, McpServerToolAttributeName);
                if (mcpAttr is null)
                    continue;

                var name = ExtractNamedStringArg(mcpAttr, "Name");
                var description =
                    ExtractNamedStringArg(mcpAttr, "Description")
                    ?? ExtractDescriptionFromSibling(method);

                if (string.IsNullOrEmpty(name))
                {
                    diagnostics.Add(
                        $"skip: {type.FullName}.{method.Name} missing Name on [McpServerTool]");
                    continue;
                }

                if (string.IsNullOrEmpty(description))
                {
                    diagnostics.Add(
                        $"{type.FullName}.{method.Name} ({name}) missing Description " +
                        $"(PPDS016 gates this upstream)");
                    description = string.Empty;
                }

                tools.Add(new ToolInfo(
                    Name: name!,
                    Description: description!,
                    Domain: InferDomain(type, name!),
                    Parameters: ExtractParameters(method),
                    ReturnType: FormatType(method.ReturnType),
                    Examples: ExtractExamples(method)));
            }
        }

        return tools;
    }

    private static string[] BuildRuntimeAssemblies(string targetAssemblyPath)
    {
        // MetadataLoadContext requires a resolver that can find every referenced
        // assembly, including the BCL. Use the current runtime directory + the
        // target's directory; that covers the common case (generator shares the
        // .NET runtime with the target).
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

    private static CustomAttributeData? FindAttribute(MemberInfo member, string attributeName)
    {
        foreach (var data in member.GetCustomAttributesData())
        {
            if (data.AttributeType.Name == attributeName)
                return data;
        }
        return null;
    }

    private static string? ExtractNamedStringArg(CustomAttributeData attribute, string name)
    {
        foreach (var arg in attribute.NamedArguments)
        {
            if (arg.MemberName == name && arg.TypedValue.Value is string s && s.Length > 0)
                return s;
        }
        return null;
    }

    private static string? ExtractDescriptionFromSibling(MemberInfo method)
    {
        // PPDS convention: [System.ComponentModel.Description("...")] sibling
        // attribute on the same method. Matches the PPDS016 analyzer's logic.
        foreach (var data in method.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != DescriptionAttributeName)
                continue;

            var ns = data.AttributeType.Namespace;
            if (ns != "System.ComponentModel")
                continue;

            if (data.ConstructorArguments.Count > 0 &&
                data.ConstructorArguments[0].Value is string s &&
                s.Length > 0)
            {
                return s;
            }
        }
        return null;
    }

    private static List<ParamInfo> ExtractParameters(MethodInfo method)
    {
        // Declaration order (AC-19 determinism) — reflect's default ordering is
        // declaration order, reinforced by sorting on MetadataToken.
        var parameters = method.GetParameters()
            .OrderBy(p => p.MetadataToken)
            .ToList();

        var result = new List<ParamInfo>(parameters.Count);
        foreach (var p in parameters)
        {
            // Skip CancellationToken — MCP does not expose it on the input schema.
            if (p.ParameterType.FullName == "System.Threading.CancellationToken")
                continue;

            var description = ExtractParameterDescription(p);
            var required = !p.HasDefaultValue && !p.IsOptional;
            result.Add(new ParamInfo(
                Name: p.Name ?? string.Empty,
                Type: FormatType(p.ParameterType),
                Required: required,
                Description: description));
        }
        return result;
    }

    private static string? ExtractParameterDescription(ParameterInfo parameter)
    {
        foreach (var data in parameter.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != DescriptionAttributeName)
                continue;
            var ns = data.AttributeType.Namespace;
            if (ns != "System.ComponentModel")
                continue;
            if (data.ConstructorArguments.Count > 0 &&
                data.ConstructorArguments[0].Value is string s &&
                s.Length > 0)
            {
                return s;
            }
        }
        return null;
    }

    private static List<ExampleInfo> ExtractExamples(MemberInfo method)
    {
        var examples = new List<ExampleInfo>();
        foreach (var data in method.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != McpToolExampleAttributeName)
                continue;

            // Constructor signature: (string input, string? expectedOutput = null)
            string? input = null;
            string? expected = null;
            if (data.ConstructorArguments.Count > 0 &&
                data.ConstructorArguments[0].Value is string inStr)
            {
                input = inStr;
            }
            if (data.ConstructorArguments.Count > 1 &&
                data.ConstructorArguments[1].Value is string outStr)
            {
                expected = outStr;
            }
            if (input is not null)
                examples.Add(new ExampleInfo(input, expected));
        }
        return examples;
    }

    /// <summary>
    /// Infers a domain string used to group tools in the index. Heuristic:
    /// 1. If the containing type's simple name is prefixed by a recognized
    ///    word (e.g., <c>Env...</c>, <c>Metadata...</c>, <c>Auth...</c>,
    ///    <c>Plugin...</c>, <c>Query...</c>, <c>Solutions...</c>), return the
    ///    lowercased prefix.
    /// 2. Else fall back to the underscore-separated prefix of the tool name
    ///    (e.g., <c>ppds_env_list</c> → <c>env</c>).
    /// 3. Else return <c>other</c>.
    /// </summary>
    private static string InferDomain(Type type, string toolName)
    {
        var typeName = type.Name;
        foreach (var prefix in new[]
        {
            "Auth", "ConnectionReferences", "CustomApis", "Data", "Env",
            "EnvironmentVariables", "ImportJobs", "Metadata", "Plugin",
            "PluginTraces", "Plugins", "Query", "ServiceEndpoints",
            "Solutions", "WebResources",
        })
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                return prefix.ToLowerInvariant();
        }

        // Fall back to parsing tool name: ppds_env_list → env
        var parts = toolName.Split('_');
        if (parts.Length >= 2 && parts[0] == "ppds")
            return parts[1].ToLowerInvariant();
        if (parts.Length >= 1)
            return parts[0].ToLowerInvariant();

        return "other";
    }

    private static string FormatType(Type t)
    {
        if (!t.IsGenericType)
            return t.Name;

        var name = t.Name;
        var tickIndex = name.IndexOf('`');
        if (tickIndex >= 0)
            name = name[..tickIndex];

        var sb = new StringBuilder();
        sb.Append(name);
        sb.Append('<');
        var args = t.GetGenericArguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(FormatType(args[i]));
        }
        sb.Append('>');
        return sb.ToString();
    }

    private static string RenderTool(ToolInfo tool)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BannerHelper.AutogeneratedBanner(ToolName));
        sb.AppendLine();
        sb.Append("# ").AppendLine(tool.Name);
        sb.AppendLine();
        sb.AppendLine(MdxEscape.Prose(tool.Description));
        sb.AppendLine();

        sb.AppendLine("## Input schema");
        sb.AppendLine();
        if (tool.Parameters.Count == 0)
        {
            sb.AppendLine("_No parameters._");
        }
        else
        {
            sb.AppendLine("| Name | Type | Required | Description |");
            sb.AppendLine("|------|------|----------|-------------|");
            foreach (var p in tool.Parameters)
            {
                sb.Append("| ").Append(p.Name)
                  .Append(" | ").Append(MdxEscape.InlineCode(p.Type))
                  .Append(" | ").Append(p.Required ? "yes" : "no")
                  .Append(" | ").Append(p.Description is null ? "" : MdxEscape.Prose(p.Description))
                  .AppendLine(" |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.Append("Returns ").Append(MdxEscape.InlineCode(tool.ReturnType)).AppendLine(".");
        sb.AppendLine();

        if (tool.Examples.Count > 0)
        {
            sb.AppendLine("## Examples");
            sb.AppendLine();
            for (var i = 0; i < tool.Examples.Count; i++)
            {
                var ex = tool.Examples[i];
                sb.Append("### Example ").Append(i + 1).AppendLine();
                sb.AppendLine();
                sb.AppendLine("Input:");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(ex.Input);
                sb.AppendLine("```");
                sb.AppendLine();
                if (ex.ExpectedOutput is not null)
                {
                    sb.AppendLine("Expected output:");
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(ex.ExpectedOutput);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private static string RenderIndex(IReadOnlyList<ToolInfo> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BannerHelper.AutogeneratedBanner(ToolName));
        sb.AppendLine();
        sb.AppendLine("# MCP Tools");
        sb.AppendLine();

        if (tools.Count == 0)
        {
            sb.AppendLine("_No tools found._");
            return sb.ToString();
        }

        // Determinism: sort domains alphabetically, tools within a domain by name.
        var grouped = tools
            .GroupBy(t => t.Domain, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            sb.Append("## ").AppendLine(group.Key);
            sb.AppendLine();
            sb.AppendLine("| Tool | Description |");
            sb.AppendLine("|------|-------------|");
            foreach (var tool in group.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var oneLine = ToOneLine(tool.Description);
                sb.Append("| [").Append(tool.Name)
                  .Append("](./tools/").Append(tool.Name).Append(".md)")
                  .Append(" | ").Append(MdxEscape.Prose(oneLine))
                  .AppendLine(" |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ToOneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var trimmed = s.Replace("\r", " ", StringComparison.Ordinal)
                       .Replace("\n", " ", StringComparison.Ordinal)
                       .Replace("|", "\\|", StringComparison.Ordinal);
        // Collapse repeated spaces.
        while (trimmed.Contains("  ", StringComparison.Ordinal))
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        return trimmed.Trim();
    }

    /// <summary>
    /// Resolver that first consults a seed list of absolute paths, then falls
    /// back to scanning the user's NuGet global package cache when an attribute
    /// references a transitively-loaded assembly that wasn't copied to the
    /// target's bin dir (common when the target was built for test purposes
    /// and its dependencies resolve via the deps.json at runtime).
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

            // Optional fallback: the NuGet global packages folder. Recursive
            // search is expensive; we lazy-scan per-name when needed.
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

            // Deep-scan the NuGet cache for this name. Pick a net8.0 / any TFM
            // candidate deterministically (sorted path order) — we are only
            // reading metadata, so any TFM's metadata suffices for attribute
            // type resolution.
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
                    // Prefer net8.0, else net9.0, else netstandard2.0, else first.
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
                    // Swallow; we'll return null and the caller decides.
                }
            }

            return null;
        }
    }

    private sealed record ToolInfo(
        string Name,
        string Description,
        string Domain,
        List<ParamInfo> Parameters,
        string ReturnType,
        List<ExampleInfo> Examples);

    private sealed record ParamInfo(
        string Name,
        string Type,
        bool Required,
        string? Description);

    private sealed record ExampleInfo(string Input, string? ExpectedOutput);
}
