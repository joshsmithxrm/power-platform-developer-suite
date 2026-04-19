using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace PPDS.DocsGen.Libs;

/// <summary>
/// Parsed representation of an XML doc file keyed by C# XML doc-comment member ID
/// (e.g. <c>T:PPDS.Foo.Bar</c>, <c>M:PPDS.Foo.Bar.Baz(System.Int32)</c>).
/// Supports <c>&lt;inheritdoc /&gt;</c> resolution: if a member's XML is
/// <c>&lt;inheritdoc /&gt;</c> (or <c>&lt;inheritdoc cref="..." /&gt;</c>), callers
/// of <see cref="ForType"/> / <see cref="ForMember"/> receive the resolved
/// documentation from the first documented ancestor in the inheritance chain
/// (explicit cref, interface match, base-class match, in that order).
/// </summary>
internal sealed class XmlDocIndex
{
    private readonly IReadOnlyDictionary<string, TypeDoc> _entries;

    private XmlDocIndex(IReadOnlyDictionary<string, TypeDoc> entries)
    {
        _entries = entries;
    }

    public static XmlDocIndex Load(string xmlPath)
    {
        var map = new Dictionary<string, TypeDoc>(StringComparer.Ordinal);
        if (!File.Exists(xmlPath))
        {
            return new XmlDocIndex(map);
        }

        var doc = XDocument.Load(xmlPath);
        foreach (var member in doc.Descendants("member"))
        {
            var name = (string?)member.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;

            var summaryEl = member.Element("summary");
            var summary = ReadText(summaryEl);
            var returns = ReadText(member.Element("returns"));
            var parameters = member.Elements("param")
                .Select(p => new ParamDoc(
                    (string?)p.Attribute("name") ?? string.Empty,
                    ReadText(p) ?? string.Empty))
                .Where(p => !string.IsNullOrEmpty(p.Name))
                .ToArray();

            var inheritEl = member.Element("inheritdoc");
            var isInheritDoc = inheritEl is not null && string.IsNullOrWhiteSpace(summary);
            var inheritCref = inheritEl is null ? null : (string?)inheritEl.Attribute("cref");

            map[name] = new TypeDoc(
                Summary: summary,
                Returns: returns,
                Params: parameters,
                IsInheritDoc: isInheritDoc,
                InheritCref: NormalizeCref(inheritCref));
        }
        return new XmlDocIndex(map);
    }

    public TypeDoc? ForType(Type type) => ResolveEntry("T:" + TypeDocId(type), resolver: () => ResolveTypeInheritance(type));

    public TypeDoc? ForMember(MemberInfo member)
    {
        var id = MemberDocId(member);
        if (id is null) return null;
        return ResolveEntry(id, resolver: () => ResolveMemberInheritance(member));
    }

    private TypeDoc? ResolveEntry(string id, Func<TypeDoc?> resolver)
    {
        if (!_entries.TryGetValue(id, out var direct)) return null;
        if (!direct.IsInheritDoc) return direct;

        // Explicit cref wins.
        if (!string.IsNullOrEmpty(direct.InheritCref)
            && _entries.TryGetValue(direct.InheritCref!, out var byCref)
            && !byCref.IsInheritDoc
            && !string.IsNullOrWhiteSpace(byCref.Summary))
        {
            return byCref;
        }

        // Implicit chain resolution (interface → base class).
        return resolver() ?? direct;
    }

    private TypeDoc? ResolveTypeInheritance(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            var doc = LookupByTypeId(iface);
            if (IsUsable(doc)) return doc;
        }
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.FullName == "System.Object") break;
            var doc = LookupByTypeId(baseType);
            if (IsUsable(doc)) return doc;
        }
        return null;
    }

    private TypeDoc? ResolveMemberInheritance(MemberInfo member)
    {
        var declaringType = member.DeclaringType;
        if (declaringType is null) return null;

        // Interface implementations first.
        foreach (var iface in declaringType.GetInterfaces())
        {
            var candidate = FindMatchingMember(iface, member);
            if (candidate is null) continue;
            var doc = LookupByMemberId(candidate);
            if (IsUsable(doc)) return doc;
        }

        // Base-class overrides.
        for (var baseType = declaringType.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.FullName == "System.Object") break;
            var candidate = FindMatchingMember(baseType, member);
            if (candidate is null) continue;
            var doc = LookupByMemberId(candidate);
            if (IsUsable(doc)) return doc;
        }

        return null;
    }

    private static bool IsUsable(TypeDoc? doc) =>
        doc is not null && !doc.IsInheritDoc && !string.IsNullOrWhiteSpace(doc.Summary);

    private TypeDoc? LookupByTypeId(Type type) =>
        _entries.TryGetValue("T:" + TypeDocId(type), out var v) ? v : null;

    private TypeDoc? LookupByMemberId(MemberInfo member)
    {
        var id = MemberDocId(member);
        return id is not null && _entries.TryGetValue(id, out var v) ? v : null;
    }

    private static MemberInfo? FindMatchingMember(Type searchIn, MemberInfo target)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        switch (target)
        {
            case MethodInfo m:
                var targetParams = m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToArray();
                foreach (var candidate in searchIn.GetMethods(flags))
                {
                    if (candidate.Name != m.Name) continue;
                    var cps = candidate.GetParameters();
                    if (cps.Length != targetParams.Length) continue;
                    var match = true;
                    for (var i = 0; i < cps.Length; i++)
                    {
                        var cpName = cps[i].ParameterType.FullName ?? cps[i].ParameterType.Name;
                        if (cpName != targetParams[i]) { match = false; break; }
                    }
                    if (match) return candidate;
                }
                return null;

            case PropertyInfo p:
                return searchIn.GetProperty(p.Name, flags);

            case EventInfo e:
                return searchIn.GetEvent(e.Name, flags);

            case FieldInfo f:
                return searchIn.GetField(f.Name, flags);

            default:
                return null;
        }
    }

    private static string? NormalizeCref(string? cref)
    {
        if (string.IsNullOrWhiteSpace(cref)) return null;
        // Docs ids already carry a "T:"/"M:"/... prefix in the XML. If the
        // cref in source was written without an explicit prefix, the C#
        // compiler emits it as "T:..." by default for types; for members it
        // emits the appropriate prefix too. Treat the value as the full id.
        return cref;
    }

    private static string? ReadText(XElement? el)
    {
        if (el is null) return null;

        var sb = new StringBuilder();
        foreach (var node in el.Nodes())
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement inner:
                    if (inner.Name.LocalName is "see" or "seealso")
                    {
                        var cref = (string?)inner.Attribute("cref");
                        var langword = (string?)inner.Attribute("langword");
                        if (!string.IsNullOrEmpty(langword))
                        {
                            sb.Append('`').Append(langword).Append('`');
                        }
                        else if (!string.IsNullOrEmpty(cref))
                        {
                            var name = cref;
                            var colon = name.IndexOf(':');
                            if (colon >= 0) name = name.Substring(colon + 1);
                            sb.Append('`').Append(name).Append('`');
                        }
                    }
                    else if (inner.Name.LocalName == "paramref" || inner.Name.LocalName == "typeparamref")
                    {
                        var pName = (string?)inner.Attribute("name");
                        if (!string.IsNullOrEmpty(pName)) sb.Append('`').Append(pName).Append('`');
                    }
                    else if (inner.Name.LocalName is "c" or "code")
                    {
                        sb.Append('`').Append(inner.Value).Append('`');
                    }
                    else
                    {
                        // Fallback: flatten inner text.
                        sb.Append(inner.Value);
                    }
                    break;
            }
        }

        var raw = sb.ToString();
        return Dedent(raw);
    }

    private static string Dedent(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        var end = lines.Length;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        if (start >= end) return string.Empty;

        var minIndent = int.MaxValue;
        for (var i = start; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var indent = 0;
            while (indent < lines[i].Length && lines[i][indent] == ' ') indent++;
            if (indent < minIndent) minIndent = indent;
        }
        if (minIndent == int.MaxValue) minIndent = 0;

        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            if (line.Length >= minIndent) line = line.Substring(minIndent);
            if (i > start) sb.Append('\n');
            sb.Append(line.TrimEnd());
        }
        return sb.ToString();
    }

    // ---- XML Doc ID construction (Ecma-335 / C# spec §D.4.2) ----

    private static string TypeDocId(Type type) => TypeDocIdForRef(type, closedGeneric: false);

    private static string TypeDocIdForRef(Type type, bool closedGeneric)
    {
        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is not null ? "``" : "`") + type.GenericParameterPosition.ToString();
        }

        if (type.IsByRef) return TypeDocIdForRef(type.GetElementType()!, closedGeneric) + "@";
        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            var suffix = rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]";
            return TypeDocIdForRef(type.GetElementType()!, closedGeneric) + suffix;
        }
        if (type.IsPointer) return TypeDocIdForRef(type.GetElementType()!, closedGeneric) + "*";

        var ns = type.Namespace;
        var name = type.Name;

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var defName = def.Name;
            var tick = defName.IndexOf('`');
            var baseName = tick < 0 ? defName : defName.Substring(0, tick);
            var rawName = string.IsNullOrEmpty(ns) ? baseName : ns + "." + baseName;

            if (closedGeneric)
            {
                var args = type.GetGenericArguments();
                return rawName + "{" + string.Join(",", args.Select(a => TypeDocIdForRef(a, true))) + "}";
            }
            return string.IsNullOrEmpty(ns) ? defName : ns + "." + defName;
        }

        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public static string? MemberDocId(MemberInfo member)
    {
        return member switch
        {
            MethodInfo m => "M:" + MethodDocId(m),
            ConstructorInfo c => "M:" + CtorDocId(c),
            PropertyInfo p => "P:" + PropertyDocId(p),
            EventInfo e => "E:" + TypeDocId(e.DeclaringType!) + "." + e.Name,
            FieldInfo f => "F:" + TypeDocId(f.DeclaringType!) + "." + f.Name,
            _ => null,
        };
    }

    private static string MethodDocId(MethodInfo m)
    {
        var declaring = TypeDocId(m.DeclaringType!);
        var name = m.Name;
        var genericSuffix = string.Empty;
        if (m.IsGenericMethod || m.IsGenericMethodDefinition)
        {
            genericSuffix = "``" + m.GetGenericArguments().Length.ToString();
        }
        var paramPart = MethodParamPart(m.GetParameters());
        return declaring + "." + name + genericSuffix + paramPart;
    }

    private static string CtorDocId(ConstructorInfo c)
    {
        var declaring = TypeDocId(c.DeclaringType!);
        return declaring + "." + "#ctor" + MethodParamPart(c.GetParameters());
    }

    private static string PropertyDocId(PropertyInfo p)
    {
        var declaring = TypeDocId(p.DeclaringType!);
        var indexParams = p.GetIndexParameters();
        var paramPart = indexParams.Length == 0 ? string.Empty : MethodParamPart(indexParams);
        return declaring + "." + p.Name + paramPart;
    }

    private static string MethodParamPart(ParameterInfo[] parameters)
    {
        if (parameters.Length == 0) return string.Empty;
        return "(" + string.Join(",", parameters.Select(p => TypeDocIdForRef(p.ParameterType, closedGeneric: true))) + ")";
    }
}

internal sealed record TypeDoc(
    string? Summary,
    string? Returns,
    IReadOnlyList<ParamDoc> Params,
    bool IsInheritDoc = false,
    string? InheritCref = null);

internal sealed record ParamDoc(string Name, string Description);
