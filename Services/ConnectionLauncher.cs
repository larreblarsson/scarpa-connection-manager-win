using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

/// <summary>
/// Replaces the Linux VTE terminal + pexpect launcher. On Windows we shell out to the
/// built-in OpenSSH client inside Windows Terminal (or conhost as a fallback).
/// </summary>
public static class ConnectionLauncher
{
    public static bool CheckHost(string host, int port, int timeoutSeconds = 3)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromSeconds(timeoutSeconds)) && client.Connected;
        }
        catch { return false; }
    }

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
        => LaunchInTerminal("ssh", BuildSshArguments(cfg), cfg, settings);

    public static void LaunchSftpCli(ServerConfig cfg, AppSettings settings)
        => LaunchInTerminal("sftp", BuildSftpArguments(cfg), cfg, settings);

    private static bool WindowsTerminalAvailable()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        return paths.Any(p =>
        {
            try { return !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "wt.exe")); }
            catch { return false; }
        });
    }

    private static void LaunchInTerminal(string exe, string args, ServerConfig cfg, AppSettings settings)
    {
        AppPaths.EnsureDirectories();

        int port = cfg.Port > 0 ? cfg.Port : 22;
        bool isHostOnline = CheckHost(cfg.Host, port, 2);
        bool usePassword = cfg.AuthMethod == "password" && !string.IsNullOrEmpty(cfg.Password);

        // 1. Password Injection via VBScript
        if (isHostOnline && usePassword)
        {
            string sendKeysPassword = cfg.Password
                .Replace("{", "{{}").Replace("}", "{}}")
                .Replace("+", "{+}").Replace("^", "{^}")
                .Replace("%", "{%}").Replace("~", "{~}")
                .Replace("(", "{(}").Replace(")", "{)}")
                .Replace("\"", "\"\"");

            string vbsFile = Path.Combine(Path.GetTempPath(), $"scarpa_auth_{Guid.NewGuid():N}.vbs");
            string vbsCode = $@"
WScript.Sleep 3000
Set ws = CreateObject(""WScript.Shell"")
ws.AppActivate ""{cfg.Name}"" 
WScript.Sleep 100
ws.SendKeys ""{sendKeysPassword}{{ENTER}}""
CreateObject(""Scripting.FileSystemObject"").DeleteFile WScript.ScriptFullName
";
            File.WriteAllText(vbsFile, vbsCode);
            Process.Start(new ProcessStartInfo("wscript.exe", $"\"{vbsFile}\"") { UseShellExecute = true });
        }

        // 2. Build a pure cmd.exe command chain
        string cmdCommand = "";
        if (!isHostOnline) cmdCommand += $"echo WARNING: {cfg.Host} is offline or unreachable. & ";
        else if (usePassword) cmdCommand += "echo Auto-authenticating with saved password... & ";

        if (cfg.LoggingEnabled)
        {
            var logPath = ResolveLogPath(cfg);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var redirect = cfg.LogMode == "append" ? ">>" : ">";
            // Pure CMD redirection
            cmdCommand += $"{exe} {args} {redirect} \"{logPath}\" 2>&1";
        }
        else
        {
            cmdCommand += $"{exe} {args}";
        }

        var useWt = settings.TerminalHost == "wt" || (settings.TerminalHost == "auto" && WindowsTerminalAvailable());

        // 3. Launch using cmd.exe /k to keep the window open after exit
        var psi = useWt
            ? new ProcessStartInfo("wt.exe", $"new-tab --title \"{cfg.Name}\" cmd.exe /k \"{cmdCommand}\"")
            : new ProcessStartInfo("cmd.exe", $"/k \"{cmdCommand}\"");

        psi.UseShellExecute = true;
        Process.Start(psi);
    }

    public static string ResolveLogPath(ServerConfig cfg)
    {
        var template = string.IsNullOrWhiteSpace(cfg.LogPath)
            ? Path.Combine(AppPaths.LogDir, "%N_%Y-%M-%D_%h%m%s.log")
            : cfg.LogPath!;

        // Detect if the user provided a static path without time variables
        // If so, automatically append the date and time variables before the extension
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
            .Replace("%N", Sanitize(cfg.Name))
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
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// <summary>Writes a temporary .rdp file and hands it to mstsc.exe.</summary>
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