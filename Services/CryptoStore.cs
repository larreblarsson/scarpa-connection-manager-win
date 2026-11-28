using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
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

    private static byte[] DeriveKey(byte[] passphraseBytes, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(passphraseBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
    }

    private static T WithSecretUtf8Bytes<T>(SecureString secret, Func<byte[], T> action)
    {
        ArgumentNullException.ThrowIfNull(secret);

        IntPtr bstr = IntPtr.Zero;
        char[]? chars = null;
        byte[]? utf8Bytes = null;
        try
        {
            bstr = Marshal.SecureStringToBSTR(secret);
            int length = secret.Length;
            chars = new char[length];
            Marshal.Copy(bstr, chars, 0, length);

            utf8Bytes = Encoding.UTF8.GetBytes(chars);
            return action(utf8Bytes);
        }
        finally
        {
            if (utf8Bytes is not null)
                CryptographicOperations.ZeroMemory(utf8Bytes);
            if (chars is not null)
                Array.Clear(chars);
            if (bstr != IntPtr.Zero)
                Marshal.ZeroFreeBSTR(bstr);
        }
    }

    public static List<ServerConfig> Load(SecureString passphrase)
    {
        return WithSecretUtf8Bytes(passphrase, LoadFromBytes);
    }

    public static List<ServerConfig> Load(string passphrase)
    {
        byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return LoadFromBytes(passphraseBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static List<ServerConfig> LoadFromBytes(byte[] passphraseBytes)
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
        byte[]? key = null;
        try
        {
            key = DeriveKey(passphraseBytes, salt);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

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
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Incorrect master passphrase.");
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Save(IEnumerable<ServerConfig> servers, SecureString passphrase)
    {
        WithSecretUtf8Bytes(passphrase, bytes =>
        {
            SaveWithBytes(servers, bytes);
            return true;
        });
    }

    public static void Save(IEnumerable<ServerConfig> servers, string passphrase)
    {
        byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            SaveWithBytes(servers, passphraseBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static void SaveWithBytes(IEnumerable<ServerConfig> servers, byte[] passphraseBytes)
    {
        AppPaths.EnsureDirectories();
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(servers, JsonOpts));
        var salt = GenerateSalt();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        byte[]? key = null;

        try
        {
            key = DeriveKey(passphraseBytes, salt);
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag);

            using var ms = new MemoryStream();
            ms.Write(Magic); ms.WriteByte(Version);
            ms.Write(salt); ms.Write(nonce); ms.Write(tag); ms.Write(cipher);

            var tmp = AppPaths.ServerFile + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, AppPaths.ServerFile, overwrite: true);
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
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

    // --- EXPORT / IMPORT METHODS ---

    public static void ExportClearText(List<ServerConfig> servers, string filePath)
    {
        string json = JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public static List<ServerConfig> ImportClearText(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<List<ServerConfig>>(json) ?? new List<ServerConfig>();
    }

    public static void ExportEncrypted(List<ServerConfig> servers, string filePath, string password)
    {
        string json = JsonSerializer.Serialize(servers);

        byte[] salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
        using var aes = Aes.Create();
        aes.Key = pbkdf2.GetBytes(32);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        using var ms = new MemoryStream();

        // Write Salt and IV to the beginning of the file so we can read them during import
        ms.Write(salt, 0, salt.Length);
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(json);
        }

        File.WriteAllBytes(filePath, ms.ToArray());
    }

    public static List<ServerConfig> ImportEncrypted(string filePath, string password)
    {
        byte[] encryptedBytes = File.ReadAllBytes(filePath);
        using var ms = new MemoryStream(encryptedBytes);

        // Extract the Salt and IV from the beginning of the file
        byte[] salt = new byte[16];
        ms.Read(salt, 0, salt.Length);

        byte[] iv = new byte[16];
        ms.Read(iv, 0, iv.Length);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
        using var aes = Aes.Create();
        aes.Key = pbkdf2.GetBytes(32);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);

        string decryptedJson = sr.ReadToEnd();

        return JsonSerializer.Deserialize<List<ServerConfig>>(decryptedJson) ?? new List<ServerConfig>();
    }
}