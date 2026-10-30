using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PPDS.Cli.Plugins.Extraction;

// Native CI exercises the real default location and distinct container identities
// through a process boundary without adding a public production cache API.
var assembly = typeof(AssemblyExtractor).Assembly;
var command = args.FirstOrDefault() ?? "";
if (command == "identity")
{
    Console.WriteLine($"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} {Environment.UserName}");
    return;
}
if (command == "seed-legacy")
{
    SeedLegacy(args.Length == 2 ? args[1] : Environment.UserName);
    return;
}
if (command is not ("create" or "reject")) throw new ArgumentException("Expected identity, seed-legacy, create, or reject.");
var originalError = Console.Error;
using var error = new StringWriter();
string? cache;
try
{
    Console.SetError(error);
    var types = args.Length == 1 ? Type.EmptyTypes : new[] { typeof(string), typeof(Action) };
    var method = typeof(AssemblyExtractor).GetMethod("GetNet462ReferenceAssemblyDirectory",
        BindingFlags.Static | BindingFlags.NonPublic, types)!;
    cache = (string?)method.Invoke(null, args.Length == 1 ? null : new object?[] { args[1], null });
}
finally { Console.SetError(originalError); }
if (command == "reject")
{
    if (cache != null || !error.ToString().Contains("Warning:") || !error.ToString().Contains("Unsafe reference-cache path '"))
        throw new InvalidOperationException($"Unsafe storage was not rejected with a path diagnostic: {cache}, {error}");
    Console.WriteLine("Rejected unsafe storage without fallback");
    return;
}
if (cache == null || error.ToString().Length != 0)
    throw new InvalidOperationException($"Protected cache unavailable: {error}");
VerifyContents(cache);
Console.WriteLine(cache);

byte[] Resource()
{
    using var source = assembly.GetManifestResourceStream("PPDS.Cli.Resources.Net462ReferenceAssemblies.zip")!;
    using var buffer = new MemoryStream();
    source.CopyTo(buffer);
    return buffer.ToArray();
}

void VerifyContents(string directory)
{
    using var stream = new MemoryStream(Resource());
    using var zip = new ZipArchive(stream);
    var expected = new HashSet<string>(StringComparer.Ordinal) { ".complete" };
    foreach (var entry in zip.Entries.Where(e => e.Name.Length != 0))
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var path = Path.Combine(directory, entry.FullName);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(buffer.ToArray()))
            throw new InvalidOperationException($"Cached bytes differ from embedded resource: {entry.FullName}");
        expected.Add(entry.FullName.Replace('/', Path.DirectorySeparatorChar));
    }
    var actual = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(directory, path)).ToHashSet(StringComparer.Ordinal);
    if (!expected.SetEquals(actual)) throw new InvalidOperationException("Unexpected/missing cache files.");
}

void SeedLegacy(string user)
{
    // Never seed a real user's legacy cache. The harness supplies a disposable temp root
    // with an explicit marker, and this command refuses existing fixture cache trees.
    var root = Environment.GetEnvironmentVariable("PPDS_CACHE_PROBE_ROOT") ?? "";
    if (!Path.IsPathFullyQualified(root) || !Regex.IsMatch(user, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)
        || !File.Exists(Path.Combine(root, ".ppds-cache-probe"))
        || Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
        throw new InvalidOperationException("Legacy seeding requires the disposable probe temp root.");
    var bytes = Resource();
    var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    var legacy = Path.Combine(root, $"ppds-{user}", "net462-ref", hash);
    if (Directory.Exists(Path.Combine(root, $"ppds-{user}")))
        throw new InvalidOperationException("Refusing to overwrite an existing legacy tree.");
    using var stream = new MemoryStream(bytes);
    using var zip = new ZipArchive(stream);
    zip.ExtractToDirectory(legacy);
    File.WriteAllBytes(Path.Combine(legacy, ".complete"), []);
    var facade = Path.Combine(legacy, "System.Runtime.dll");
    var poisoned = File.ReadAllBytes(facade);
    poisoned[poisoned.Length / 2] ^= 0xff; // Same length defeats completeness-only checks.
    File.WriteAllBytes(facade, poisoned);
    File.WriteAllText(Path.Combine(legacy, "Injected.dll"), "untrusted fixture");
    Console.WriteLine(legacy);
}
