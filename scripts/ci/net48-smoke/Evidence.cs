using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Net48Smoke;

// Only ciphertext and a deliberately small status summary leave the isolated runner.
internal sealed class Evidence(string publicKey, string outputDirectory) : IDisposable
{
    private readonly RSA rsa = LoadKey(publicKey);
    private readonly List<object> events = [];
    public void Add(string kind, object data)
    {
        events.Add(new { at = DateTimeOffset.UtcNow, kind, data });
        Save();
    }
    private static RSA LoadKey(string path)
    {
        var key = RSA.Create();
        key.ImportFromPem(File.ReadAllText(path));
        return key;
    }
    private void Save()
    {
        Directory.CreateDirectory(outputDirectory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(events);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("net48-approved-smoke-v1"));
            var envelope = new { schemaVersion = 1, algorithm = "RSA-OAEP-SHA256/AES-256-GCM",
                wrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256), nonce, tag, ciphertext };
            var path = Path.Combine(outputDirectory, "evidence.enc.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(envelope));
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Dispose() => rsa.Dispose();
}

internal static class Scope
{
    public const string Package = "ppds1450_61e24a05";
    public const string Assembly = "Ppds1450Smoke_61e24a05";
    public const string PluginType = "Ppds1450Smoke.MarkerPlugin";
    public const string Step = "MarkerPlugin: Create of account";
    public const string Prefix = "PPDS1450_61e24a05_";
    public const string V1 = "98db0d59c3624e2ff98b453d1b4fe54fcd042808f8bf2361b34129a6dc2dbaab";
    public const string V2 = "0f6a266ff244f63840171ae71f52925efbbcbb8729bbacb273a21d276c7f33a8";
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashValue(string value) => Hash(Encoding.UTF8.GetBytes(value.ToLowerInvariant().TrimEnd('/')));
    public static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }
}