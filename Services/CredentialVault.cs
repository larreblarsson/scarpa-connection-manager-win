using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

/// <summary>
/// "Remember me" for the master passphrase. The Linux build uses libsecret;
/// on Windows we use DPAPI (CurrentUser scope) so only this Windows account can read it.
/// </summary>
public static class CredentialVault
{
    private static string File_ => Path.Combine(AppPaths.DataDir, "master.dpapi");

    public static string? Get()
    {
        try
        {
            if (!File.Exists(File_)) return null;
            var blob = File.ReadAllBytes(File_);
            var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public static void Set(string passphrase)
    {
        AppPaths.EnsureDirectories();
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(passphrase), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(File_, blob);
    }

    public static void Clear()
    {
        try { if (File.Exists(File_)) File.Delete(File_); } catch { }
    }
}