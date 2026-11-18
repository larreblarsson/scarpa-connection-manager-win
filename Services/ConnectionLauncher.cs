using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ScarpaConnectionManager.Services;

public static class ConnectionLauncher
{
    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
        
    public static string ResolveLogPath(ServerConfig cfg)
    {
        // 1. Use the exact path the user typed, or fallback to "SessionName.log"
        var template = string.IsNullOrWhiteSpace(cfg.LogPath)
            ? Path.Combine(AppPaths.LogDir, "%N.log")
            : cfg.LogPath!;

        // 2. Apply substitutions ONLY if the user explicitly typed the % variables
        var now = DateTime.Now;
        return template
            .Replace("%N", Sanitize(cfg.Name ?? "Session"))
            .Replace("%H", Sanitize(cfg.Host))
            .Replace("%U", Sanitize(cfg.User))
            .Replace("%Y", now.ToString("yyyy"))
            .Replace("%M", now.ToString("MM"))
            .Replace("%D", now.ToString("dd"))
            .Replace("%h", now.ToString("HH"))
            .Replace("%m", now.ToString("mm"))
            .Replace("%s", now.ToString("ss"));
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }

    public static void LaunchRdp(ServerConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{cfg.Host}:{cfg.RdpPort}");
        if (!string.IsNullOrWhiteSpace(cfg.User)) sb.AppendLine($"username:s:{cfg.User}");
        sb.AppendLine($"audiomode:i:{(cfg.RdpAudio ? 0 : 2)}");
        sb.AppendLine($"redirectclipboard:i:{(cfg.RdpClipboard ? 1 : 0)}");
        sb.AppendLine($"authentication level:i:{(cfg.RdpIgnoreCert ? 0 : 2)}");
        sb.AppendLine("prompt for credentials:i:1");

        if (cfg.RdpResolution.Equals("Full Screen", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("screen mode id:i:2");
        }
        else
        {
            var wh = cfg.RdpResolution.Split('x');
            if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
            {
                sb.AppendLine("screen mode id:i:1");
                sb.AppendLine($"desktopwidth:i:{w}");
                sb.AppendLine($"desktopheight:i:{h}");
            }
        }

        if (cfg.RdpRedirectDrive && !string.IsNullOrWhiteSpace(cfg.RdpDrivePath))
            sb.AppendLine("drivestoredirect:s:*");

        AppPaths.EnsureDirectories();
        var file = Path.Combine(Path.GetTempPath(), $"scarpa_{Guid.NewGuid():N}.rdp");
        File.WriteAllText(file, sb.ToString());
        Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{file}\"") { UseShellExecute = true });
    }
}