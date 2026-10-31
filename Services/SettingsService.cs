using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

public static class SettingsService
{
    private static JsonSerializerOptions GetJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new SecureStringDpapiConverter());
        return options;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, GetJsonOptions()) ?? new AppSettings();
            }
        }
        catch { /* corrupt settings must never block startup */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        AppPaths.EnsureDirectories();
        string json = JsonSerializer.Serialize(settings, GetJsonOptions());
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}

/// <summary>
/// Custom JSON Converter that encrypts SecureString at rest using Windows DPAPI (Current User Scope).
/// </summary>
public class SecureStringDpapiConverter : JsonConverter<SecureString>
{
    public override SecureString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? base64Encrypted = reader.GetString();
        if (string.IsNullOrEmpty(base64Encrypted)) return null;

        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(base64Encrypted);

            // Decrypt using the current Windows user's master key
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            string plainText = Encoding.UTF8.GetString(decryptedBytes);

            var secureString = new SecureString();
            foreach (char c in plainText)
            {
                secureString.AppendChar(c);
            }
            secureString.MakeReadOnly();
            return secureString;
        }
        catch
        {
            // Failsafe: If decryption fails (e.g., copied to another PC or user profile), return null
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, SecureString value, JsonSerializerOptions options)
    {
        if (value == null || value.Length == 0)
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        IntPtr unmanagedString = IntPtr.Zero;
        try
        {
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(value);
            string plainText = Marshal.PtrToStringUni(unmanagedString) ?? string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

            // Encrypt using the current Windows user's master key
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            writer.WriteStringValue(Convert.ToBase64String(encryptedBytes));
        }
        finally
        {
            if (unmanagedString != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }
}