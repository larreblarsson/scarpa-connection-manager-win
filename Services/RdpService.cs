using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
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
                string connectHost = previousFwd == null ? jump.Host : "127.0.0.1";
                int connectPort = previousFwd == null ? (jump.Port > 0 ? jump.Port : 22) : (int)previousFwd.BoundPort;

                var client = new SshClient(connectHost, connectPort, jump.User, jump.Password);
                client.Connect();
                activeClients.Add(client);

                bool isLastJump = jump == config.RdpJumpHosts.Last();

                string fwdDestHost = isLastJump ? config.Host : config.RdpJumpHosts[config.RdpJumpHosts.IndexOf(jump) + 1].Host;
                int fwdDestPort = isLastJump ? (config.RdpPort > 0 ? config.RdpPort : 3389) : config.RdpJumpHosts[config.RdpJumpHosts.IndexOf(jump) + 1].Port;

                var fwd = new ForwardedPortLocal("127.0.0.1", 0, fwdDestHost, (uint)fwdDestPort);
                client.AddForwardedPort(fwd);
                fwd.Start();
                activeForwards.Add(fwd);

                previousFwd = fwd;
            }

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

        string rdpFilePath = Path.Combine(Path.GetTempPath(), $"scarpa_{targetHost.Replace(".", "_")}_{targetPort}.rdp");
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

        // 3.5 SUPPRESS "UNKNOWN PUBLISHER" WARNING POPUPS
        try
        {
            // 1. Classic Bypass: Approves Clipboard/Drive redirection
            using (var devKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client\LocalDevices"))
            {
                if (devKey != null)
                {
                    devKey.SetValue(targetHost, 127, RegistryValueKind.DWord);
                    devKey.SetValue($"{targetHost}:{targetPort}", 127, RegistryValueKind.DWord);
                }
            }

            // 2. April 2026 Update Bypass: Reverts the new strict RDP security dialog to the classic behavior
            using (var consentKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client"))
            {
                consentKey?.SetValue("RdpLaunchConsentAccepted", 1, RegistryValueKind.DWord);
            }

            using (var policyKey = Registry.CurrentUser.CreateSubKey(@"Software\Policies\Microsoft\Windows NT\Terminal Services\Client"))
            {
                policyKey?.SetValue("RedirectionWarningDialogVersion", 1, RegistryValueKind.DWord);
            }
        }
        catch { }

        // 4. LAUNCH RDP CLIENT
        Process? rdpProcess = null;
        bool isModernApp = false;

        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string[] executablesToTry =
        {
            Path.Combine(sys32, "mstsc.exe"),
            Path.Combine(winDir, "Sysnative", "mstsc.exe"),
            Path.Combine(winDir, "System32", "mstsc.exe")
        };

        // ATTEMPT 1: Direct File Execution for Classic RDP (mstsc.exe)
        foreach (var exe in executablesToTry)
        {
            if (File.Exists(exe))
            {
                rdpProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"\"{rdpFilePath}\"",
                    UseShellExecute = false
                });
                break;
            }
        }

        // ATTEMPT 2: App Execution Alias for the new "Windows App"
        if (rdpProcess == null)
        {
            try
            {
                rdpProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "windowsapp.exe",
                    Arguments = $"\"{rdpFilePath}\"",
                    UseShellExecute = true
                });

                isModernApp = true;
            }
            catch { }
        }

        // ATTEMPT 3: Neither exists. Tell the user to install it.
        if (rdpProcess == null)
        {
            try { if (File.Exists(rdpFilePath)) File.Delete(rdpFilePath); } catch { }
            throw new Exception("No RDP application is installed on the Windows OS.\n\nPlease download and install the Windows App using this link:\nhttps://go.microsoft.com/fwlink/?linkid=2247659");
        }

        // 5. BACKGROUND CLEANUP
        _ = Task.Run(async () =>
        {
            if (rdpProcess != null)
            {
                try { rdpProcess.WaitForExit(); } catch { }
            }

            if (isModernApp) await Task.Delay(15000);
            else await Task.Delay(3000);

            try { if (File.Exists(rdpFilePath)) File.Delete(rdpFilePath); } catch { }

            if (!string.IsNullOrEmpty(autoLogonPass))
            {
                try { Process.Start(new ProcessStartInfo { FileName = "cmdkey", Arguments = $"/delete:TERMSRV/{targetHost}", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); } catch { }
            }

            for (int i = activeForwards.Count - 1; i >= 0; i--) { try { activeForwards[i].Stop(); } catch { } }
            for (int i = activeClients.Count - 1; i >= 0; i--) { try { activeClients[i].Dispose(); } catch { } }
        });
    }
}