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
	
		string script = "";
	
		if (cfg.AuthMethod == "password" && !string.IsNullOrEmpty(cfg.Password))
		{
			// Print visual feedback to the console window so the user sees something is happening
			script += "Write-Host 'Auto-authenticating with saved password: ' -NoNewline -ForegroundColor DarkGray; ";
	
			// Escape special characters for SendKeys
			string sendKeysPassword = cfg.Password
				.Replace("{", "{{}").Replace("}", "{}}")
				.Replace("+", "{+}").Replace("^", "{^}")
				.Replace("%", "{%}").Replace("~", "{~}")
				.Replace("(", "{(}").Replace(")", "{)}")
				.Replace("'", "''");
	
			// Background job to inject the password after the prompt appears
			script += $"$job = Start-Job -ScriptBlock {{ Start-Sleep -Milliseconds 1200; $wshell = New-Object -ComObject WScript.Shell; $wshell.SendKeys('{sendKeysPassword}{{ENTER}}') }}; ";
		}
	
		if (cfg.LoggingEnabled)
		{
			var logPath = ResolveLogPath(cfg);
			Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
			var redirect = cfg.LogMode == "append" ? "Tee-Object -Append -FilePath" : "Tee-Object -FilePath";
			script += $"{exe} {args} 2>&1 | {redirect} '{logPath}'; ";
		}
		else
		{
			script += $"{exe} {args}; ";
		}
	
		if (cfg.AuthMethod == "password" && !string.IsNullOrEmpty(cfg.Password))
		{
			script += "Remove-Job -Job $job -Force -ErrorAction SilentlyContinue; ";
		}
	
		// Encode the script in Base64
		var bytes = System.Text.Encoding.Unicode.GetBytes(script);
		var encodedCommand = Convert.ToBase64String(bytes);
		var psCommand = $"-NoLogo -NoExit -EncodedCommand {encodedCommand}";
	
		var useWt = settings.TerminalHost == "wt" ||
					(settings.TerminalHost == "auto" && WindowsTerminalAvailable());
	
		var psi = useWt
			? new ProcessStartInfo("wt.exe", $"new-tab --title \"{cfg.Name}\" -- powershell.exe {psCommand}")
			: new ProcessStartInfo("powershell.exe", psCommand);
	
		psi.UseShellExecute = true;
		System.Diagnostics.Process.Start(psi);
	}

    public static string ResolveLogPath(ServerConfig cfg)
    {
        var template = string.IsNullOrWhiteSpace(cfg.LogPath)
            ? Path.Combine(AppPaths.LogDir, "%N_%Y-%M-%D_%h%m%s.log")
            : cfg.LogPath!;
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