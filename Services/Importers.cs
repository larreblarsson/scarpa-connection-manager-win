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
        var doc = XDocument.Load(filePath);

        void Walk(XElement node, string folderPath)
        {
            foreach (var key in node.Elements("key"))
            {
                var name = (string?)key.Attribute("name") ?? "";
                var host = key.Elements("string").FirstOrDefault(e => (string?)e.Attribute("name") == "Hostname")?.Value;

                if (!string.IsNullOrWhiteSpace(host))
                {
                    var user = key.Elements("string").FirstOrDefault(e => (string?)e.Attribute("name") == "Username")?.Value ?? "";
                    var portRaw = key.Elements("dword").FirstOrDefault(e => (string?)e.Attribute("name") == "[SSH2] Port")?.Value
                               ?? key.Elements("dword").FirstOrDefault(e => (string?)e.Attribute("name") == "Port")?.Value;
                    var port = 22;
                    if (!string.IsNullOrWhiteSpace(portRaw))
                        int.TryParse(portRaw, System.Globalization.NumberStyles.HexNumber, null, out port);
                    if (port <= 0) port = 22;

                    result.Add(new ServerConfig
                    {
                        Name = name,
                        Host = host!,
                        User = user,
                        Port = port,
                        AuthMethod = "password",
                        Folder = folderPath
                    });
                }
                else
                {
                    Walk(key, string.IsNullOrEmpty(folderPath) ? name : $"{folderPath}/{name}");
                }
            }
        }

        var sessions = doc.Root?.Elements("key").FirstOrDefault(e => (string?)e.Attribute("name") == "Sessions");
        if (sessions != null) Walk(sessions, AppPaths.RootFolder);
        return result;
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

    /// <summary>Parses a MobaXterm .mxtsessions file (INI-ish, '#' separated fields).</summary>
    public static List<ServerConfig> FromMobaXterm(string path)
    {
        var result = new List<ServerConfig>();
        var folder = AppPaths.RootFolder;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var section = line.Trim('[', ']');
                var parts = section.Split('_');
                folder = parts.Length > 1 ? parts[^1] : AppPaths.RootFolder;
                if (string.IsNullOrWhiteSpace(folder) || folder == "Bookmarks") folder = AppPaths.RootFolder;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var name = line[..eq];
            var fields = line[(eq + 1)..].Split('%');
            if (fields.Length < 5) continue;
            // #109#0%host%port%user%...
            var head = fields[0].Split('#');
            if (head.Length < 2 || head[1] != "109") continue;

            int.TryParse(fields[2], out var port);
            result.Add(new ServerConfig
            {
                Name = name,
                Host = fields[1],
                Port = port == 0 ? 22 : port,
                User = fields.Length > 3 ? fields[3] : "",
                AuthMethod = "password",
                Folder = folder
            });
        }
        return result;
    }
}