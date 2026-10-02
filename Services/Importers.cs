using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

/// <summary>Ports of parse_securecrt_xml / parse_putty_reg / MobaXterm import.</summary>
public static class Importers
{
    public static List<ServerConfig> FromSecureCrtXml(string filePath)
    {
        var result = new List<ServerConfig>();
        var doc = System.Xml.Linq.XDocument.Load(filePath);

        void Walk(System.Xml.Linq.XElement node, string folderPath)
        {
            foreach (var key in node.Elements("key"))
            {
                var name = (string?)key.Attribute("name") ?? "";
                var host = key.Elements("string").FirstOrDefault(e => (string?)e.Attribute("name") == "Hostname")?.Value;

                if (!string.IsNullOrWhiteSpace(host))
                {
                    var user = key.Elements("string").FirstOrDefault(e => (string?)e.Attribute("name") == "Username")?.Value ?? "";

                    // NEW: Extract SecureCRT's proprietary encrypted password string
                    var passwordRaw = key.Elements("string").FirstOrDefault(e =>
                        (string?)e.Attribute("name") == "Password V2" ||
                        (string?)e.Attribute("name") == "Password" ||
                        (string?)e.Attribute("name") == "[SSH2] Password V2" ||
                        (string?)e.Attribute("name") == "[SSH2] Password")?.Value ?? "";

                    // Pass it through our brand new native decrypter
                    var password = DecryptSecureCrtPassword(passwordRaw);

                    var portRaw = key.Elements("dword").FirstOrDefault(e => (string?)e.Attribute("name") == "[SSH2] Port")?.Value
                               ?? key.Elements("dword").FirstOrDefault(e => (string?)e.Attribute("name") == "Port")?.Value;

                    var port = 22;
                    if (!string.IsNullOrWhiteSpace(portRaw))
                    {
                        if (!int.TryParse(portRaw, out port))
                        {
                            int.TryParse(portRaw, System.Globalization.NumberStyles.HexNumber, null, out port);
                        }
                    }
                    if (port <= 0) port = 22;

                    result.Add(new ServerConfig
                    {
                        Name = name,
                        Host = host!,
                        User = user,
                        Password = password,
                        Port = port,
                        AuthMethod = "password",
                        Folder = string.IsNullOrEmpty(folderPath) ? AppPaths.RootFolder : folderPath
                    });
                }
                else
                {
                    Walk(key, string.IsNullOrEmpty(folderPath) ? name : $"{folderPath}/{name}");
                }
            }
        }

        var sessions = doc.Root?.Elements("key").FirstOrDefault(e => (string?)e.Attribute("name") == "Sessions");
        if (sessions != null) Walk(sessions, "");

        return result;
    }

    // --- NATIVE SECURECRT DECRYPTOR ---
    private static string DecryptSecureCrtPassword(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        try
        {
            // SecureCRT V2 uses AES-256-CBC
            if (raw.StartsWith("02:"))
            {
                string hex = raw.Substring(3);
                byte[] ciphered = Convert.FromHexString(hex);

                byte[] key = System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>());
                byte[] iv = new byte[16];

                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;

                using var decryptor = aes.CreateDecryptor();
                byte[] decrypted = decryptor.TransformFinalBlock(ciphered, 0, ciphered.Length);

                int len = BitConverter.ToInt32(decrypted, 0);
                if (len > 0 && len <= decrypted.Length - 4)
                {
                    string decoded = System.Text.Encoding.UTF8.GetString(decrypted, 4, len);

                    // FIX: Strip the invisible null-terminator (\0) that SecureCRT leaves behind!
                    return decoded.Replace("\0", "");
                }
            }
            // SecureCRT V1 uses a double-pass Blowfish-CBC
            else if (raw.StartsWith("u", StringComparison.OrdinalIgnoreCase))
            {
                string hex = raw.Substring(1);
                byte[] ciphered = Convert.FromHexString(hex);
                if (ciphered.Length <= 8) return "";

                byte[] key1 = { 0x24, 0xa6, 0x3d, 0xde, 0x5b, 0xd3, 0xb3, 0x82, 0x9c, 0x7e, 0x06, 0xf4, 0x08, 0x16, 0xaa, 0x07 };
                byte[] key2 = { 0x5f, 0xb0, 0x45, 0xa2, 0x94, 0x17, 0xd9, 0x16, 0xc6, 0xc6, 0xa2, 0xff, 0x06, 0x41, 0x82, 0xb7 };
                byte[] iv = new byte[8];

                var engine1 = new Org.BouncyCastle.Crypto.BufferedBlockCipher(new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.BlowfishEngine()));
                engine1.Init(false, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key1), iv));
                byte[] step1 = engine1.DoFinal(ciphered);

                byte[] trimmed = new byte[step1.Length - 8];
                Array.Copy(step1, 4, trimmed, 0, trimmed.Length);

                var engine2 = new Org.BouncyCastle.Crypto.BufferedBlockCipher(new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(new Org.BouncyCastle.Crypto.Engines.BlowfishEngine()));
                engine2.Init(false, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key2), iv));
                byte[] step2 = engine2.DoFinal(trimmed);

                string result = System.Text.Encoding.Unicode.GetString(step2);

                // FIX: Clean null terminators here as well
                int nullIdx = result.IndexOf('\0');
                return nullIdx >= 0 ? result.Substring(0, nullIdx) : result.Replace("\0", "");
            }
            else if (!raw.StartsWith("03:"))
            {
                // Unencrypted passwords (if any)
                return raw.Replace("\0", "");
            }
        }
        catch
        {
            // Fail silently on decryption errors
        }

        return "";
    }

    /// <summary>Reads live PuTTY sessions straight out of HKCU (Windows-native advantage).</summary>
    public static List<ServerConfig> FromPuttyRegistry()
    {
        var result = new List<ServerConfig>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (root == null) return result;

        foreach (var sessionName in root.GetSubKeyNames())
        {
            using var s = root.OpenSubKey(sessionName);
            if (s == null) continue;
            var host = s.GetValue("HostName") as string;
            if (string.IsNullOrWhiteSpace(host)) continue;

            result.Add(new ServerConfig
            {
                Name = Uri.UnescapeDataString(sessionName.Replace("%20", " ")),
                Host = host!,
                User = s.GetValue("UserName") as string ?? "",
                Port = Convert.ToInt32(s.GetValue("PortNumber") ?? 22),
                KeyFile = s.GetValue("PublicKeyFile") as string,
                AuthMethod = string.IsNullOrWhiteSpace(s.GetValue("PublicKeyFile") as string) ? "password" : "key_file",
                Folder = AppPaths.RootFolder
            });
        }
        return result;
    }

    /// <summary>Parses an exported PuTTY .reg file.</summary>
    public static List<ServerConfig> FromPuttyRegFile(string path)
    {
        var result = new List<ServerConfig>();
        ServerConfig? current = null;
        var sectionRx = new Regex(@"^\[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\(.+)\]$");

        foreach (var rawLine in File.ReadAllLines(path, Encoding.Unicode.GetPreamble().Length > 0 ? Encoding.Unicode : Encoding.UTF8))
        {
            var line = rawLine.Trim();
            var m = sectionRx.Match(line);
            if (m.Success)
            {
                if (current != null && !string.IsNullOrWhiteSpace(current.Host)) result.Add(current);
                current = new ServerConfig
                {
                    Name = Uri.UnescapeDataString(m.Groups[1].Value.Replace("%20", " ")),
                    Folder = AppPaths.RootFolder
                };
                continue;
            }
            if (current == null || !line.Contains('=')) continue;

            var idx = line.IndexOf('=');
            var key = line[..idx].Trim('"');
            var val = line[(idx + 1)..].Trim();

            switch (key)
            {
                case "HostName": current.Host = val.Trim('"'); break;
                case "UserName": current.User = val.Trim('"'); break;
                case "PublicKeyFile":
                    current.KeyFile = val.Trim('"').Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(current.KeyFile)) current.AuthMethod = "key_file";
                    break;
                case "PortNumber":
                    if (val.StartsWith("dword:") && int.TryParse(val[6..], System.Globalization.NumberStyles.HexNumber, null, out var p))
                        current.Port = p;
                    break;
            }
        }
        if (current != null && !string.IsNullOrWhiteSpace(current.Host)) result.Add(current);
        return result;
    }

    /// <summary>Parses a MobaXterm .mxtsessions or .mobaconf file.</summary>
    public static (List<ServerConfig> Servers, List<string> Folders) FromMobaXterm(string path)
    {
        var servers = new List<ServerConfig>();
        var folders = new List<string>();
        var currentFolder = "";
        bool inBookmarksSection = false;

        // FIX: Explicitly read using Latin1 encoding to handle ANSI characters (Å, Ä, Ö) correctly
        foreach (var raw in File.ReadAllLines(path, System.Text.Encoding.Latin1))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var section = line.Trim('[', ']');
                if (section.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase))
                {
                    inBookmarksSection = true;
                    currentFolder = "";
                }
                else
                {
                    inBookmarksSection = false;
                }
                continue;
            }

            if (!inBookmarksSection) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var name = line[..eq];
            var value = line[(eq + 1)..].Trim();

            if (name.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                currentFolder = value.Replace('\\', '/');
                if (!string.IsNullOrEmpty(currentFolder) && !folders.Contains(currentFolder))
                {
                    folders.Add(currentFolder);
                }
                continue;
            }

            var fields = value.Split('%');
            if (fields.Length < 5) continue;

            var head = fields[0].Split('#');
            if (head.Length < 3) continue;

            if (head[1] != "109" && head[1] != "91") continue;

            int.TryParse(fields[2], out var port);

            servers.Add(new ServerConfig
            {
                Name = name,
                Host = fields[1],
                Port = port == 0 ? 22 : port,
                User = fields.Length > 3 ? fields[3] : "",
                AuthMethod = "password",
                Folder = string.IsNullOrEmpty(currentFolder) ? AppPaths.RootFolder : currentFolder
            });
        }

        return (servers, folders);
    }
}