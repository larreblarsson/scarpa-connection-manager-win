using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

/// <summary>
/// Replaces the Linux build's "gpg --symmetric --cipher-algo AES256" store with a
/// pure-.NET AES-256-GCM container so no external binaries are needed on Windows.
///
/// File layout:  MAGIC(6) | ver(1) | salt(16) | nonce(12) | tag(16) | ciphertext
/// </summary>
public static class CryptoStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCARPA");
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int Pbkdf2Iterations = 260_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static string HashPassphrase(string passphrase, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return Convert.ToHexString(kdf.GetBytes(32)).ToLowerInvariant();
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    public static List<ServerConfig> Load(string passphrase)
    {
        var path = AppPaths.ServerFile;
        if (!File.Exists(path)) return new List<ServerConfig>();

        var raw = File.ReadAllBytes(path);
        if (raw.Length < Magic.Length + 1 + SaltSize + NonceSize + TagSize)
            throw new InvalidDataException("Server file is corrupted (too short).");
        for (var i = 0; i < Magic.Length; i++)
            if (raw[i] != Magic[i]) throw new InvalidDataException("Server file is not a Scarpa vault.");

        var offset = Magic.Length + 1;
        var salt = raw.AsSpan(offset, SaltSize).ToArray(); offset += SaltSize;
        var nonce = raw.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
        var tag = raw.AsSpan(offset, TagSize).ToArray(); offset += TagSize;
        var cipher = raw.AsSpan(offset).ToArray();

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(passphrase, salt), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Incorrect master passphrase.");
        }

        var list = JsonSerializer.Deserialize<List<ServerConfig>>(Encoding.UTF8.GetString(plain))
                   ?? new List<ServerConfig>();
        foreach (var s in list)
        {
            if (string.IsNullOrWhiteSpace(s.Folder)) s.Folder = AppPaths.RootFolder;
            s.PortForwards ??= new List<PortForward>();
            s.AutoSequence ??= new List<SequenceStep>();
        }
        return list;
    }

    public static void Save(IEnumerable<ServerConfig> servers, string passphrase)
    {
        AppPaths.EnsureDirectories();
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(servers, JsonOpts));
        var salt = GenerateSalt();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(DeriveKey(passphrase, salt), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        using var ms = new MemoryStream();
        ms.Write(Magic); ms.WriteByte(Version);
        ms.Write(salt); ms.Write(nonce); ms.Write(tag); ms.Write(cipher);

        var tmp = AppPaths.ServerFile + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, AppPaths.ServerFile, overwrite: true);
        CryptographicOperations.ZeroMemory(plain);
    }

    /// <summary>Plain-text JSON export (used by the Export menu, mirrors the Linux export).</summary>
	public static void ExportPlain(IEnumerable<ServerConfig> servers, string path)
	{
		var root = new ScarpaExportRoot { Servers = servers.ToList() };
		
		// Ensure the Windows export matches the Linux snake_case format
		var opts = new JsonSerializerOptions 
		{ 
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};
		File.WriteAllText(path, JsonSerializer.Serialize(root, opts));
	}
	
	public static ScarpaExportRoot ImportPlain(string path)
	{
		var json = File.ReadAllText(path);
		var opts = new JsonSerializerOptions 
		{ 
			PropertyNameCaseInsensitive = true,
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
		};
	
		try
		{
			var root = JsonSerializer.Deserialize<ScarpaExportRoot>(json, opts);
			return root ?? new ScarpaExportRoot();
		}
		catch (JsonException originalException)
		{
			try
			{
				// Fallback for raw arrays
				var list = JsonSerializer.Deserialize<List<ServerConfig>>(json, opts) ?? new List<ServerConfig>();
				return new ScarpaExportRoot { Servers = list };
			}
			catch
			{
				throw originalException; 
			}
		}
	}
}