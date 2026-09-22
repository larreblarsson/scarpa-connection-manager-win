using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ScarpaConnectionManager.Services;

/// <summary>
/// Handles launching native WinUI 3 terminal dialogs for SSH/SFTP sessions,
/// as well as background processes and RDP sessions.
/// </summary>
public static class ConnectionLauncher
{
    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public static string BuildSshArguments(ServerConfig cfg)
    {
        var parts = new List<string>
        {
            $"-oUserKnownHostsFile={Quote(AppPaths.KnownHostsFile)}",
            "-oStrictHostKeyChecking=accept-new"
        };

        if (cfg.AuthMethod == "password")
            parts.Add("-oPubkeyAuthentication=no");

        if (cfg.AntiIdleEnabled)
        {
            parts.Add($"-oServerAliveInterval={Math.Max(5, cfg.AntiIdleInterval)}");
            parts.Add("-oServerAliveCountMax=3");
        }

        foreach (var rule in cfg.PortForwards)
        {
            switch (rule.Type)
            {
                case "Dynamic": parts.Add($"-D {rule.SourcePort}"); break;
                case "Local": parts.Add($"-L {rule.SourcePort}:{rule.DestHost}:{rule.DestPort}"); break;
                case "Remote": parts.Add($"-R {rule.SourcePort}:{rule.DestHost}:{rule.DestPort}"); break;
            }
        }

        if (cfg.AuthMethod == "key_file" && !string.IsNullOrWhiteSpace(cfg.KeyFile))
            parts.Add($"-i {Quote(cfg.KeyFile!)}");

        parts.Add("-t");
        parts.Add($"-p {cfg.Port}");
        parts.Add($"{cfg.User}@{cfg.Host}");
        return string.Join(" ", parts);
    }

    public static string BuildSftpArguments(ServerConfig cfg)
    {
        var parts = new List<string>
        {
            $"-oUserKnownHostsFile={Quote(AppPaths.KnownHostsFile)}",
            "-oStrictHostKeyChecking=accept-new",
            "-oBatchMode=no"
        };
        if (cfg.AuthMethod == "password") parts.Add("-oPubkeyAuthentication=no");
        if (cfg.AuthMethod == "key_file" && !string.IsNullOrWhiteSpace(cfg.KeyFile))
            parts.Add($"-i {Quote(cfg.KeyFile!)}");
        parts.Add($"-P {cfg.Port}");
        parts.Add($"{cfg.User}@{cfg.Host}");
        return string.Join(" ", parts);
    }

    public static void LaunchSsh(ServerConfig cfg, AppSettings settings)
    {
        string? logPath = cfg.LoggingEnabled ? ResolveLogPath(cfg) : null;
        var terminalWindow = new scarpa_connection_manager_win.Dialogs.TerminalDialog(cfg, logPath);
        terminalWindow.Title = cfg.Name ?? "SSH Session";
        terminalWindow.Activate();
    }

    public static void LaunchSftpCli(ServerConfig cfg, AppSettings settings)
    {
        string? logPath = cfg.LoggingEnabled ? ResolveLogPath(cfg) : null;
        var terminalWindow = new scarpa_connection_manager_win.Dialogs.TerminalDialog(cfg, logPath, isSftp: true);
        terminalWindow.Title = cfg.Name != null ? $"{cfg.Name} (SFTP)" : "SFTP Session";
        terminalWindow.Activate();
    }

    public static Process LaunchSshBackground(ServerConfig cfg)
    {
        AppPaths.EnsureDirectories();
        TerminalLogger? sessionLogger = null;

        if (cfg.LoggingEnabled)
        {
            bool append = cfg.LogMode == "append";
            string folderPath = Path.GetDirectoryName(ResolveLogPath(cfg)) ?? AppPaths.LogDir;

            sessionLogger = new TerminalLogger(
                folderPath,
                Sanitize(cfg.Name ?? "Session"),
                append,
                includeTimestamps: true
            );
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = BuildSshArguments(cfg),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var sshProcess = new Process { StartInfo = psi };

        sshProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                sessionLogger?.LogOutput(e.Data + Environment.NewLine);
            }
        };

        sshProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                sessionLogger?.LogOutput("ERROR: " + e.Data + Environment.NewLine);
            }
        };

        sshProcess.Start();
        sshProcess.BeginOutputReadLine();
        sshProcess.BeginErrorReadLine();

        return sshProcess;
    }

    public static string ResolveLogPath(ServerConfig cfg)
    {
        var template = string.IsNullOrWhiteSpace(cfg.LogPath)
            ? Path.Combine(AppPaths.LogDir, "%N_%Y-%M-%D_%h%m%s.log")
            : cfg.LogPath!;

        if (!template.Contains("%h") && !template.Contains("%s"))
        {
            var dir = Path.GetDirectoryName(template);
            if (string.IsNullOrWhiteSpace(dir)) dir = AppPaths.LogDir;

            var name = Path.GetFileNameWithoutExtension(template);
            var ext = Path.GetExtension(template);
            if (string.IsNullOrEmpty(ext)) ext = ".log";

            template = Path.Combine(dir, $"{name}_%Y%M%D_%h%m%s{ext}");
        }

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