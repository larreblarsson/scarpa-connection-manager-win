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

        // 2. SPOOF THE SYSTEM'S DEFAULT.RDP TEMPLATE
        var rdpLines = new List<string>
        {
            $"full address:s:{targetHost}" + (targetPort == 3389 ? "" : $":{targetPort}"),
            $"username:s:{(string.IsNullOrEmpty(config.User) ? "" : config.User)}",
            $"redirectclipboard:i:{(config.RdpClipboard ? 1 : 0)}",
            $"audiomode:i:{(config.RdpAudio ? 0 : 2)}",
            $"authentication level:i:{(config.RdpIgnoreCert ? 0 : 2)}",
            
            // Explicitly disable unused devices to minimize security footprint
            "redirectprinters:i:0",
            "redirectsmartcards:i:0",
            "redirectwebauthn:i:0"
        };

        // Screen Resolution
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

        // Drive Mapping
        if (config.RdpRedirectDrive && !string.IsNullOrWhiteSpace(config.RdpDrivePath))
        {
            string rawDriveStr = config.RdpDrivePath.Trim();
            if (rawDriveStr == "*") rdpLines.Add("drivestoredirect:s:*");
            else
            {
                var drives = rawDriveStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Replace(":\\", "").Replace("\\", "").Replace(":", "").Trim() + ":");
                rdpLines.Add($"drivestoredirect:s:{string.Join(";", drives)}");
            }
        }

        // Setup paths for the Default.rdp spoofing
        string docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string defaultRdpPath = Path.Combine(docsFolder, "Default.rdp");
        string backupRdpPath = Path.Combine(docsFolder, "Default_backup.rdp");

        // PREVENT ACCESS DENIED: Clear hidden/readonly attributes before touching the file
        if (File.Exists(defaultRdpPath))
        {
            try
            {
                File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                File.Copy(defaultRdpPath, backupRdpPath, true);
            }
            catch { }
        }

        // Now it is safe to write our spoofed template
        await File.WriteAllLinesAsync(defaultRdpPath, rdpLines);

        // Re-hide the file as Windows expects
        try { File.SetAttributes(defaultRdpPath, File.GetAttributes(defaultRdpPath) | FileAttributes.Hidden); } catch { }

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
            // 1. Classic Bypass: Approves ALL redirection by using -1 (0xFFFFFFFF DWORD bitmask)
            using (var devKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client\LocalDevices"))
            {
                if (devKey != null)
                {
                    devKey.SetValue(targetHost, -1, RegistryValueKind.DWord);
                    devKey.SetValue($"{targetHost}:{targetPort}", -1, RegistryValueKind.DWord);
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

        // 4. LAUNCH RDP CLIENT NATIVELY
        // By using /v instead of passing a file, Windows treats this as a trusted manual 
        // connection and quietly absorbs the drive mappings from our Default.rdp template
        Process? rdpProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "mstsc.exe",
            Arguments = $"/v:{targetHost}" + (targetPort == 3389 ? "" : $":{targetPort}"),
            UseShellExecute = false
        });

        // 5. BACKGROUND CLEANUP & RESTORE
        _ = Task.Run(async () =>
        {
            if (rdpProcess != null)
            {
                try { rdpProcess.WaitForExit(); } catch { }
            }

            // Give MSTSC a moment to fully close before reverting files
            await Task.Delay(3000);

            // Restore the user's original Default.rdp
            try
            {
                if (File.Exists(backupRdpPath))
                {
                    // Remove hidden attribute so we can overwrite/delete safely
                    File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                    File.Copy(backupRdpPath, defaultRdpPath, true);
                    File.Delete(backupRdpPath);
                    // Re-hide the restored default.rdp
                    File.SetAttributes(defaultRdpPath, File.GetAttributes(defaultRdpPath) | FileAttributes.Hidden);
                }
                else
                {
                    File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                    File.Delete(defaultRdpPath);
                }
            }
            catch { }

            // Delete credentials
            if (!string.IsNullOrEmpty(autoLogonPass))
            {
                try { Process.Start(new ProcessStartInfo { FileName = "cmdkey", Arguments = $"/delete:TERMSRV/{targetHost}", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); } catch { }
            }

            // Kill SSH Tunnels
            for (int i = activeForwards.Count - 1; i >= 0; i--) { try { activeForwards[i].Stop(); } catch { } }
            for (int i = activeClients.Count - 1; i >= 0; i--) { try { activeClients[i].Dispose(); } catch { } }
        });
    }
}