using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using PPDS.DocsGen.Common;

namespace PPDS.DocsGen.Libs;

/// <summary>
/// Reflects over a single PPDS library assembly and emits one markdown file
/// per documented, customer-facing public type plus a per-package index.
/// See <c>specs/docs-generation.md</c> Surface-Specific §Libraries (AC-16, AC-19).
/// </summary>
public sealed class LibraryReferenceGenerator : IReferenceGenerator
{
    private const string ToolName = "libs-reflect";

    public Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var assemblyPath = Path.GetFullPath(input.SourceAssemblyPath);
        var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");

        var packageName = Path.GetFileNameWithoutExtension(assemblyPath);
        var packageShort = packageName.StartsWith("PPDS.", StringComparison.Ordinal)
            ? packageName.Substring("PPDS.".Length)
            : packageName;

        var xmlDocs = XmlDocIndex.Load(xmlPath);

        using var mlc = CreateLoadContext(assemblyPath);
        var assembly = mlc.LoadFromAssemblyPath(assemblyPath);

        var files = new List<GeneratedFile>();
        var diagnostics = new List<string>();
        var indexEntries = new List<IndexEntry>();

        var types = assembly.GetExportedTypes()
            .Where(t => !IsHidden(t))
            .OrderBy(t => t.Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var type in types)
        {
            ct.ThrowIfCancellationRequested();

            var typeDoc = xmlDocs.ForType(type);
            if (typeDoc is null || string.IsNullOrWhiteSpace(typeDoc.Summary))
            {
                if (TryFallbackSummary(typeDoc, out var typeFallback, out var typePointer))
                {
                    diagnostics.Add(
                        $"{packageName}: type '{type.FullName}' uses <inheritdoc /> but base documentation is unresolvable (target: {typePointer}); rendering fallback pointer.");
                    typeDoc = typeDoc! with { Summary = typeFallback };
                }
                else
                {
                    diagnostics.Add(
                        $"{packageName}: type '{type.FullName}' has no <summary> and is not marked [EditorBrowsable(Never)]; skipping.");
                    continue;
                }
            }

            var nsSubPath = NamespaceSubPath(type, packageName);
            var relPath = string.IsNullOrEmpty(nsSubPath)
                ? $"{packageShort}/{type.Name}.md"
                : $"{packageShort}/{nsSubPath}/{type.Name}.md";

            var (typeMd, memberDiagnostics) = RenderType(type, typeDoc, xmlDocs, packageName);
            diagnostics.AddRange(memberDiagnostics);
            files.Add(new GeneratedFile(relPath.Replace('\\', '/'), typeMd));

            indexEntries.Add(new IndexEntry(
                Name: type.Name,
                Kind: TypeKind(type),
                Summary: typeDoc.Summary!,
                RelativeLink: string.IsNullOrEmpty(nsSubPath) ? $"./{type.Name}.md" : $"./{nsSubPath}/{type.Name}.md"));
        }

        files.Add(new GeneratedFile(
            $"{packageShort}/_index.md",
            RenderIndex(packageShort, indexEntries)));

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return Task.FromResult(new GenerationResult(files, diagnostics));
    }

    private static MetadataLoadContext CreateLoadContext(string assemblyPath)
    {
        var resolver = new FallbackAssemblyResolver(BuildSeedPaths(assemblyPath));
        return new MetadataLoadContext(resolver, "System.Private.CoreLib");
    }

    private static string[] BuildSeedPaths(string targetAssemblyPath)
    {
        // MetadataLoadContext needs every referenced assembly (including the BCL) to be resolvable.
        // Seed with the current runtime dir + the target assembly's directory.
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetAssemblyPath))!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll")) paths.Add(dll);
        if (Directory.Exists(targetDir))
        {
            foreach (var dll in Directory.EnumerateFiles(targetDir, "*.dll")) paths.Add(dll);
        }
        paths.Add(Path.GetFullPath(targetAssemblyPath));
        return paths.ToArray();
    }

    /// <summary>
    /// Resolver that first consults a seed list of absolute paths, then falls back to scanning
    /// the NuGet global package cache for any transitively-referenced assembly not copied next
    /// to the target. Mirrors the pattern in mcp-reflect so both generators behave consistently
    /// when a library references third-party NuGet assemblies that only exist in the global cache.
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
            if (Directory.Exists(nugetHome)) _searchDirs.Add(nugetHome);
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
                var resolvedFromNuget = TryResolveFromNugetCache(nugetHome, name);
                if (resolvedFromNuget is not null)
                {
                    _byName[name] = resolvedFromNuget;
                    return context.LoadFromAssemblyPath(resolvedFromNuget);
                }
            }

            return null;
        }

        /// <summary>
        /// Targeted NuGet-cache lookup: first probe the conventional
        /// <c>{nugetHome}/{assemblyName}/</c> subtree (fast — bounded to one
        /// package), only falling back to a full recursive scan when the
        /// assembly simple name does not match its package id. Within the
        /// matched set, prefer the newest package version and then the
        /// newest TFM (net10.0 → net9.0 → net8.0 → netstandard2.0 → any).
        /// </summary>
        private static string? TryResolveFromNugetCache(string nugetHome, string simpleName)
        {
            try
            {
                var targetedRoot = Path.Combine(nugetHome, simpleName.ToLowerInvariant());
                List<string> matches;

                if (Directory.Exists(targetedRoot))
                {
                    matches = Directory.EnumerateFiles(
                        targetedRoot, simpleName + ".dll", SearchOption.AllDirectories).ToList();
                }
                else
                {
                    // Assembly simple name != package id — rare but legal.
                    // Fall back to a full scan; this is the slow path and
                    // only happens for unconventional packages.
                    matches = Directory.EnumerateFiles(
                        nugetHome, simpleName + ".dll", SearchOption.AllDirectories).ToList();
                }

                if (matches.Count == 0) return null;

                // Score each match: (tfm-rank, version-rank). Higher is better.
                // Pick the newest version within the highest-priority TFM.
                return matches
                    .Select(p => new { Path = p, Tfm = TfmRank(p), Version = VersionFromPath(p) })
                    .OrderByDescending(m => m.Tfm)
                    .ThenByDescending(m => m.Version)
                    .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                    .First().Path;
            }
            catch
            {
                // Swallow; returning null lets the caller surface the
                // unresolvable assembly with its original error.
                return null;
            }
        }

        private static int TfmRank(string path)
        {
            // Higher rank = more preferred. Matched against the path segment
            // so "net10.0" does not spuriously match inside "net1.0" etc.
            if (path.Contains(Path.DirectorySeparatorChar + "net10.0" + Path.DirectorySeparatorChar)
                || path.Contains(Path.AltDirectorySeparatorChar + "net10.0" + Path.AltDirectorySeparatorChar)) return 40;
            if (path.Contains(Path.DirectorySeparatorChar + "net9.0" + Path.DirectorySeparatorChar)
                || path.Contains(Path.AltDirectorySeparatorChar + "net9.0" + Path.AltDirectorySeparatorChar)) return 30;
            if (path.Contains(Path.DirectorySeparatorChar + "net8.0" + Path.DirectorySeparatorChar)
                || path.Contains(Path.AltDirectorySeparatorChar + "net8.0" + Path.AltDirectorySeparatorChar)) return 20;
            if (path.Contains(Path.DirectorySeparatorChar + "netstandard2.0" + Path.DirectorySeparatorChar)
                || path.Contains(Path.AltDirectorySeparatorChar + "netstandard2.0" + Path.AltDirectorySeparatorChar)) return 10;
            return 0;
        }

        private static Version VersionFromPath(string path)
        {
            // NuGet cache layout: {nugetHome}/{pkg}/{version}/lib/{tfm}/{dll}.
            // Walk segments from the DLL up and pick the first segment that
            // parses as a Version. Fallback to 0.0.0 so unparseable versions
            // sort last but do not crash.
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var seg in parts)
            {
                if (Version.TryParse(seg.Split('-')[0], out var v))
                {
                    return v;
                }
            }
            return new Version(0, 0, 0);
        }
    }

    private static bool IsHidden(Type type)
    {
        foreach (var attr in type.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName == typeof(EditorBrowsableAttribute).FullName
                && attr.ConstructorArguments.Count == 1
                && attr.ConstructorArguments[0].Value is int state
                && state == (int)EditorBrowsableState.Never)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsMemberHidden(MemberInfo member)
    {
        foreach (var attr in member.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName == typeof(EditorBrowsableAttribute).FullName
                && attr.ConstructorArguments.Count == 1
                && attr.ConstructorArguments[0].Value is int state
                && state == (int)EditorBrowsableState.Never)
            {
                return true;
            }
        }
        return false;
    }

    private static string NamespaceSubPath(Type type, string packageName)
    {
        var ns = type.Namespace ?? string.Empty;
        if (ns.Equals(packageName, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        if (ns.StartsWith(packageName + ".", StringComparison.Ordinal))
        {
            return ns.Substring(packageName.Length + 1).Replace('.', '/');
        }
        return ns.Replace('.', '/');
    }

    private static string TypeKind(Type type)
    {
        if (type.IsEnum) return "Enum";
        if (type.IsInterface) return "Interface";
        if (type.IsValueType) return "Struct";
        if (IsRecord(type)) return "Record";
        return "Class";
    }

    private static bool IsRecord(Type type)
    {
        // Records expose a compiler-generated <Clone>$ method with the CLR name "<Clone>$".
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "<Clone>$");
    }

    private static (string Markdown, List<string> Diagnostics) RenderType(
        Type type, TypeDoc typeDoc, XmlDocIndex xmlDocs, string packageName)
    {
        var sb = new StringBuilder();
        var diags = new List<string>();

        sb.Append(BannerHelper.AutogeneratedBanner(ToolName)).Append('\n').Append('\n');
        sb.Append("# ").Append(type.Name).Append('\n').Append('\n');
        sb.Append("- Namespace: ").Append(MdxEscape.InlineCode(type.Namespace ?? string.Empty)).Append('\n');
        sb.Append("- Assembly: ").Append(MdxEscape.InlineCode(packageName)).Append('\n');
        sb.Append("- Kind: ").Append(TypeKind(type)).Append('\n');

        if (type.BaseType is { } baseType
            && baseType.FullName != "System.Object"
            && baseType.FullName != "System.ValueType"
            && baseType.FullName != "System.Enum")
        {
            sb.Append("- Base: ").Append(MdxEscape.InlineCode(FormatTypeRef(baseType))).Append('\n');
        }

        var interfaces = type.GetInterfaces()
            .OrderBy(i => i.FullName ?? i.Name, StringComparer.Ordinal)
            .ToList();
        if (interfaces.Count > 0)
        {
            sb.Append("- Implements: ");
            for (var i = 0; i < interfaces.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(MdxEscape.InlineCode(FormatTypeRef(interfaces[i])));
            }
            sb.Append('\n');
        }

        sb.Append('\n').Append("## Summary").Append('\n').Append('\n');
        sb.Append(MdxEscape.Prose(typeDoc.Summary!.Trim())).Append('\n');

        var members = CollectMembers(type, xmlDocs, diags);
        if (members.Count > 0)
        {
            sb.Append('\n').Append("## Members").Append('\n');

            var groupOrder = new[] { "Constructors", "Methods", "Properties", "Events", "Fields" };
            foreach (var group in groupOrder)
            {
                var inGroup = members.Where(m => m.Group == group)
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.Signature, StringComparer.Ordinal)
                    .ToList();
                if (inGroup.Count == 0) continue;

                sb.Append('\n').Append("### ").Append(group).Append('\n').Append('\n');
                foreach (var m in inGroup)
                {
                    sb.Append("#### ").Append(m.Heading).Append('\n').Append('\n');
                    sb.Append(MdxEscape.InlineCode(m.Signature)).Append('\n').Append('\n');
                    sb.Append(MdxEscape.Prose(m.Summary.Trim())).Append('\n');

                    foreach (var p in m.Params)
                    {
                        sb.Append('\n').Append("- Param ").Append(MdxEscape.InlineCode(p.Name))
                            .Append(": ").Append(MdxEscape.Prose(p.Description.Trim())).Append('\n');
                    }

                    if (!string.IsNullOrWhiteSpace(m.Returns))
                    {
                        sb.Append('\n').Append("- Returns: ").Append(MdxEscape.Prose(m.Returns!.Trim())).Append('\n');
                    }

                    sb.Append('\n');
                }
            }
        }

        return (sb.ToString(), diags);
    }

    private static List<MemberRow> CollectMembers(Type type, XmlDocIndex xmlDocs, List<string> diags)
    {
        var rows = new List<MemberRow>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var isRecord = IsRecord(type);

        foreach (var ctor in type.GetConstructors(flags))
        {
            if (IsMemberHidden(ctor)) continue;
            if (IsCompilerGenerated(ctor)) continue;
            // Record synthesizes a copy-ctor with no XML doc; skip silently.
            if (isRecord && IsRecordSynthesizedCtor(ctor)) continue;
            var doc = xmlDocs.ForMember(ctor);
            var summary = ResolveMemberSummary(doc, type, "constructor", SignatureOf(ctor), diags);
            if (summary is null) continue;
            rows.Add(new MemberRow(
                Group: "Constructors",
                Name: ".ctor",
                Heading: $"{type.Name}({FormatParams(ctor.GetParameters())})",
                Signature: SignatureOf(ctor),
                Summary: summary,
                Params: doc?.Params ?? Array.Empty<ParamDoc>(),
                Returns: null));
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName) continue; // skip property/event accessors
            if (IsMemberHidden(method)) continue;
            if (IsCompilerGenerated(method)) continue;
            // Records synthesize ToString/GetHashCode/Equals/PrintMembers/Deconstruct.
            if (isRecord && IsRecordSynthesizedMethod(method)) continue;
            var doc = xmlDocs.ForMember(method);
            var summary = ResolveMemberSummary(doc, type, "method", method.Name, diags);
            if (summary is null) continue;
            rows.Add(new MemberRow(
                Group: "Methods",
                Name: method.Name,
                Heading: method.Name,
                Signature: SignatureOf(method),
                Summary: summary,
                Params: doc?.Params ?? Array.Empty<ParamDoc>(),
                Returns: doc?.Returns));
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (IsMemberHidden(prop)) continue;
            // Record synthesizes EqualityContract; not part of the author's surface.
            if (isRecord && prop.Name == "EqualityContract") continue;
            var doc = xmlDocs.ForMember(prop);
            var summary = ResolveMemberSummary(doc, type, "property", prop.Name, diags);
            if (summary is null) continue;
            rows.Add(new MemberRow(
                Group: "Properties",
                Name: prop.Name,
                Heading: prop.Name,
                Signature: SignatureOf(prop),
                Summary: summary,
                Params: Array.Empty<ParamDoc>(),
                Returns: null));
        }

        foreach (var evt in type.GetEvents(flags))
        {
            if (IsMemberHidden(evt)) continue;
            var doc = xmlDocs.ForMember(evt);
            var summary = ResolveMemberSummary(doc, type, "event", evt.Name, diags);
            if (summary is null) continue;
            rows.Add(new MemberRow(
                Group: "Events",
                Name: evt.Name,
                Heading: evt.Name,
                Signature: SignatureOf(evt),
                Summary: summary,
                Params: Array.Empty<ParamDoc>(),
                Returns: null));
        }

        foreach (var field in type.GetFields(flags))
        {
            if (IsMemberHidden(field)) continue;
            // Every enum carries an internal-layout sentinel field `value__`; not part of the surface.
            if (type.IsEnum && field.Name == "value__") continue;
            var doc = xmlDocs.ForMember(field);
            var summary = ResolveMemberSummary(doc, type, "field", field.Name, diags);
            if (summary is null) continue;
            rows.Add(new MemberRow(
                Group: "Fields",
                Name: field.Name,
                Heading: field.Name,
                Signature: SignatureOf(field),
                Summary: summary,
                Params: Array.Empty<ParamDoc>(),
                Returns: null));
        }

        return rows;
    }

    private static string? ResolveMemberSummary(TypeDoc? doc, Type type, string kind, string label, List<string> diags)
    {
        if (doc is not null && !string.IsNullOrWhiteSpace(doc.Summary))
        {
            return doc.Summary;
        }
        if (TryFallbackSummary(doc, out var fallback, out var pointer))
        {
            diags.Add($"{type.FullName}: {kind} '{label}' uses <inheritdoc /> but base documentation is unresolvable (target: {pointer}); rendering fallback pointer.");
            return fallback;
        }
        diags.Add($"{type.FullName}: {kind} '{label}' has no <summary>; skipping.");
        return null;
    }

    private static bool TryFallbackSummary(TypeDoc? doc, out string summary, out string pointer)
    {
        summary = string.Empty;
        pointer = string.Empty;
        if (doc is null) return false;
        if (string.IsNullOrEmpty(doc.UnresolvedInheritTarget)) return false;
        pointer = doc.UnresolvedInheritTarget!;
        summary = $"*(inherited from `{pointer}`)*";
        return true;
    }

    private static bool IsCompilerGenerated(MemberInfo member)
    {
        foreach (var attr in member.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
                return true;
        }
        // C# uses '<' in synthesized method names (e.g. "<Clone>$" on records).
        return member.Name.StartsWith('<');
    }

    private static bool IsRecordSynthesizedMethod(MethodInfo m)
    {
        // Records synthesize ToString/GetHashCode/Equals/PrintMembers/Deconstruct — none carry
        // XML docs, so skip silently to keep output focused on the author's surface.
        return m.Name switch
        {
            "ToString" or "GetHashCode" or "PrintMembers" or "Deconstruct" or "Equals" => true,
            _ => false,
        };
    }

    private static bool IsRecordSynthesizedCtor(ConstructorInfo c)
    {
        // The record copy-constructor takes exactly one parameter of the declaring type.
        var parameters = c.GetParameters();
        return parameters.Length == 1
            && parameters[0].ParameterType.FullName == c.DeclaringType!.FullName;
    }

    private static string RenderIndex(string packageShort, List<IndexEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(BannerHelper.AutogeneratedBanner(ToolName)).Append('\n').Append('\n');
        sb.Append("# ").Append(packageShort).Append(" reference").Append('\n').Append('\n');

        if (entries.Count == 0)
        {
            sb.Append("_No customer-facing types are currently documented in this library._").Append('\n');
            return sb.ToString();
        }

        var groupOrder = new[] { "Interface", "Class", "Record", "Struct", "Enum" };
        var groupHeadings = new Dictionary<string, string>
        {
            ["Interface"] = "Interfaces",
            ["Class"] = "Classes",
            ["Record"] = "Records",
            ["Struct"] = "Structs",
            ["Enum"] = "Enums",
        };

        foreach (var group in groupOrder)
        {
            var inGroup = entries.Where(e => e.Kind == group)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
            if (inGroup.Count == 0) continue;

            sb.Append("## ").Append(groupHeadings[group]).Append('\n').Append('\n');
            sb.Append("| Type | Summary |").Append('\n');
            sb.Append("|------|---------|").Append('\n');
            foreach (var e in inGroup)
            {
                var oneLine = FirstLine(e.Summary);
                sb.Append("| [").Append(e.Name).Append("](").Append(e.RelativeLink).Append(") | ")
                    .Append(MdxEscape.Prose(oneLine)).Append(" |").Append('\n');
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string FirstLine(string s)
    {
        var trimmed = s.Trim();
        var nl = trimmed.IndexOf('\n');
        var line = nl < 0 ? trimmed : trimmed.Substring(0, nl).TrimEnd();
        return line.Replace('|', '/');
    }

    private static string SignatureOf(ConstructorInfo ctor) =>
        $"{ctor.DeclaringType!.Name}({FormatParams(ctor.GetParameters())})";

    private static string SignatureOf(MethodInfo m) =>
        $"{FormatTypeRef(m.ReturnType)} {m.Name}{FormatGenericParams(m)}({FormatParams(m.GetParameters())})";

    private static string SignatureOf(PropertyInfo p) =>
        $"{FormatTypeRef(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : string.Empty)}{(p.CanWrite ? "set; " : string.Empty)}}}";

    private static string SignatureOf(EventInfo e) =>
        $"event {FormatTypeRef(e.EventHandlerType ?? typeof(object))} {e.Name}";

    private static string SignatureOf(FieldInfo f) =>
        $"{FormatTypeRef(f.FieldType)} {f.Name}";

    private static string FormatParams(ParameterInfo[] parameters)
    {
        if (parameters.Length == 0) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(FormatTypeRef(parameters[i].ParameterType)).Append(' ').Append(parameters[i].Name);
        }
        return sb.ToString();
    }

    private static string FormatGenericParams(MethodInfo m)
    {
        if (!m.IsGenericMethodDefinition && !m.IsGenericMethod) return string.Empty;
        var args = m.GetGenericArguments();
        if (args.Length == 0) return string.Empty;
        return "<" + string.Join(", ", args.Select(a => a.Name)) + ">";
    }

    private static string FormatTypeRef(Type t)
    {
        if (t.IsArray) return FormatTypeRef(t.GetElementType()!) + "[]";
        if (t.IsByRef) return FormatTypeRef(t.GetElementType()!);
        if (t.IsGenericParameter) return t.Name;
        if (!t.IsGenericType) return SimpleName(t);

        var def = t.GetGenericTypeDefinition();
        var rawName = SimpleName(def);
        var tick = rawName.IndexOf('`');
        var baseName = tick < 0 ? rawName : rawName.Substring(0, tick);
        var args = t.GetGenericArguments();
        return baseName + "<" + string.Join(", ", args.Select(FormatTypeRef)) + ">";
    }

    private static string SimpleName(Type t)
    {
        return t.FullName switch
        {
            "System.Void" => "void",
            "System.String" => "string",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Boolean" => "bool",
            "System.Object" => "object",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Byte" => "byte",
            "System.Char" => "char",
            "System.Decimal" => "decimal",
            _ => t.Name,
        };
    }

    private sealed record IndexEntry(string Name, string Kind, string Summary, string RelativeLink);

    private sealed record MemberRow(
        string Group,
        string Name,
        string Heading,
        string Signature,
        string Summary,
        IReadOnlyList<ParamDoc> Params,
        string? Returns);
}
