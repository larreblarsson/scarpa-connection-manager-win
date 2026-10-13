using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Renci.SshNet;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

public static class RdpService
{
    public static async Task LaunchAsync(ServerConfig config, string? password)
    {
        string targetHost = config.Host;
        int targetPort = config.RdpPort > 0 ? config.RdpPort : 3389;

        var activeClients = new List<SshClient>();
        var activeForwards = new List<ForwardedPortLocal>();

        // 1. NESTED SSH TUNNELS FOR JUMP HOSTS
        if (config.RdpJumpHosts != null && config.RdpJumpHosts.Any())
        {
            ForwardedPortLocal? previousFwd = null;

            foreach (var jump in config.RdpJumpHosts)
            {
                // If it's the first jump, connect to its host. Otherwise, connect to the local port forwarded by the previous jump.
                string connectHost = previousFwd == null ? jump.Host : "127.0.0.1";
                int connectPort = previousFwd == null ? (jump.Port > 0 ? jump.Port : 22) : (int)previousFwd.BoundPort;

                var client = new SshClient(connectHost, connectPort, jump.User, jump.Password);
                client.Connect();
                activeClients.Add(client);

                bool isLastJump = jump == config.RdpJumpHosts.Last();

                // Last jump forwards to the final RDP server. Middle jumps forward to the NEXT jump host.
                string fwdDestHost = isLastJump ? config.Host : config.RdpJumpHosts[config.RdpJumpHosts.IndexOf(jump) + 1].Host;
                int fwdDestPort = isLastJump ? (config.RdpPort > 0 ? config.RdpPort : 3389) : config.RdpJumpHosts[config.RdpJumpHosts.IndexOf(jump) + 1].Port;

                // Pick a random local port by setting it to 0
                var fwd = new ForwardedPortLocal("127.0.0.1", 0, fwdDestHost, (uint)fwdDestPort);
                client.AddForwardedPort(fwd);
                fwd.Start();
                activeForwards.Add(fwd);

                previousFwd = fwd;
            }

            // Redirect the RDP client to the final local tunnel port
            targetHost = "127.0.0.1";
            targetPort = (int)previousFwd!.BoundPort;
        }

        // 2. GENERATE THE .RDP FILE
        var rdpLines = new List<string>
        {
            $"full address:s:{targetHost}:{targetPort}",
            $"username:s:{(string.IsNullOrEmpty(config.User) ? "" : config.User)}",
            $"redirectclipboard:i:{(config.RdpClipboard ? 1 : 0)}",
            $"audiomode:i:{(config.RdpAudio ? 0 : 2)}",
            $"authentication level:i:{(config.RdpIgnoreCert ? 0 : 2)}"
        };

        if (config.RdpResolution == "Full Screen") rdpLines.Add("screen mode id:i:2");
        else if (!string.IsNullOrWhiteSpace(config.RdpResolution) && config.RdpResolution.Contains("x"))
        {
            var parts = config.RdpResolution.Split('x');
            if (parts.Length == 2)
            {
                rdpLines.Add("screen mode id:i:1");
                rdpLines.Add($"desktopwidth:i:{parts[0].Trim()}");
                rdpLines.Add($"desktopheight:i:{parts[1].Trim()}");
            }
        }

        if (config.RdpRedirectDrive && !string.IsNullOrWhiteSpace(config.RdpDrivePath))
        {
            string drive = config.RdpDrivePath.Replace(":\\", "").Replace(":", "");
            rdpLines.Add($"drivestoredirect:s:{drive}:");
        }

        string rdpFilePath = Path.Combine(Path.GetTempPath(), $"scarpa_rdp_{Guid.NewGuid()}.rdp");
        await File.WriteAllLinesAsync(rdpFilePath, rdpLines);

        // 3. SECURELY INJECT CREDENTIALS FOR AUTO-LOGIN
        string? autoLogonPass = password ?? config.Password;
        if (!string.IsNullOrEmpty(autoLogonPass))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/generic:TERMSRV/{targetHost} /user:{config.User} /pass:{autoLogonPass}",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit();
        }

        // 4. LAUNCH RDP CLIENT
        var mstscProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "mstsc.exe",
            Arguments = $"\"{rdpFilePath}\""
        });

        // 5. BACKGROUND CLEANUP (Triggers when the user closes the RDP window)
        _ = Task.Run(() =>
        {
            mstscProcess?.WaitForExit();

            try { if (File.Exists(rdpFilePath)) File.Delete(rdpFilePath); } catch { }

            if (!string.IsNullOrEmpty(autoLogonPass))
            {
                Process.Start(new ProcessStartInfo { FileName = "cmdkey", Arguments = $"/delete:TERMSRV/{targetHost}", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit();
            }

            // Close SSH Tunnels backwards
            for (int i = activeForwards.Count - 1; i >= 0; i--) { try { activeForwards[i].Stop(); } catch { } }
            for (int i = activeClients.Count - 1; i >= 0; i--) { try { activeClients[i].Dispose(); } catch { } }
        });
    }
}