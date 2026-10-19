using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Win32;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

public static class RdpService
{
    public static async Task LaunchAsync(ServerConfig config, string? password)
    {
        // targetHost and targetPort are now perfectly provided by JumpServerService (e.g. 127.0.0.1:61210)
        string targetHost = config.Host;
        int targetPort = config.RdpPort > 0 ? config.RdpPort : 3389;

        Debug.WriteLine($"[RdpService] Initiating RDP Launch sequence for {targetHost}:{targetPort}...");

        // 1. SPOOF THE SYSTEM'S DEFAULT.RDP TEMPLATE
        Debug.WriteLine($"[RdpService] Generating Default.rdp...");
        var rdpLines = new List<string>
        {
            $"full address:s:{targetHost}" + (targetPort == 3389 ? "" : $":{targetPort}"),
            $"username:s:{(string.IsNullOrEmpty(config.User) ? "" : config.User)}",
            $"redirectclipboard:i:{(config.RdpClipboard ? 1 : 0)}",
            $"audiomode:i:{(config.RdpAudio ? 0 : 2)}",
            "transport type:i:1",
            $"authentication level:i:{(config.RdpIgnoreCert ? 0 : 2)}",
            "redirectprinters:i:0",
            "redirectsmartcards:i:0",
            "redirectwebauthn:i:0"
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
            string rawDriveStr = config.RdpDrivePath.Trim();
            if (rawDriveStr == "*") rdpLines.Add("drivestoredirect:s:*");
            else
            {
                var drives = rawDriveStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Replace(":\\", "").Replace("\\", "").Replace(":", "").Trim() + ":");
                rdpLines.Add($"drivestoredirect:s:{string.Join(";", drives)}");
            }
        }

        string docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string defaultRdpPath = Path.Combine(docsFolder, "Default.rdp");
        string backupRdpPath = Path.Combine(docsFolder, "Default_backup.rdp");

        if (File.Exists(defaultRdpPath))
        {
            try
            {
                File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                File.Copy(defaultRdpPath, backupRdpPath, true);
            }
            catch { }
        }

        await File.WriteAllLinesAsync(defaultRdpPath, rdpLines);
        try { File.SetAttributes(defaultRdpPath, File.GetAttributes(defaultRdpPath) | FileAttributes.Hidden); } catch { }

        // 2. SECURELY INJECT CREDENTIALS
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

        // 3. SUPPRESS POPUPS
        try
        {
            using (var devKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client\LocalDevices"))
            {
                if (devKey != null)
                {
                    devKey.SetValue(targetHost, -1, RegistryValueKind.DWord);
                    devKey.SetValue($"{targetHost}:{targetPort}", -1, RegistryValueKind.DWord);
                }
            }
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

        // 4. LAUNCH RDP
        Debug.WriteLine($"[RdpService] Launching mstsc.exe /v:{targetHost}:{targetPort}...");
        Process.Start(new ProcessStartInfo
        {
            FileName = "mstsc.exe",
            Arguments = $"/v:{targetHost}" + (targetPort == 3389 ? "" : $":{targetPort}"),
            UseShellExecute = false
        });

        // 5. WAIT FOR RDP SESSION TO CLOSE
        Debug.WriteLine($"[RdpService] Waiting for MSTSC to establish a TCP connection on port {targetPort}...");
        bool isConnected = false;

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            if (IsTunnelActive(targetPort))
            {
                Debug.WriteLine("[RdpService] TCP Connection successfully established! Holding method open...");
                isConnected = true;
                break;
            }
        }

        if (!isConnected)
        {
            Debug.WriteLine("[RdpService] TIMEOUT: mstsc.exe never established a TCP connection.");
        }

        if (isConnected)
        {
            while (IsTunnelActive(targetPort))
            {
                await Task.Delay(3000);
            }
            Debug.WriteLine("[RdpService] TCP Connection closed by user. Initiating cleanup...");
            await Task.Delay(2000);
        }

        // 6. CLEANUP
        try
        {
            if (File.Exists(backupRdpPath))
            {
                File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                File.Copy(backupRdpPath, defaultRdpPath, true);
                File.Delete(backupRdpPath);
                File.SetAttributes(defaultRdpPath, File.GetAttributes(defaultRdpPath) | FileAttributes.Hidden);
            }
            else
            {
                File.SetAttributes(defaultRdpPath, FileAttributes.Normal);
                File.Delete(defaultRdpPath);
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(autoLogonPass))
        {
            try { Process.Start(new ProcessStartInfo { FileName = "cmdkey", Arguments = $"/delete:TERMSRV/{targetHost}", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(); } catch { }
        }

        Debug.WriteLine("[RdpService] RDP Launch sequence and cleanup complete.");
    }

    private static bool IsTunnelActive(int port)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var connections = properties.GetActiveTcpConnections();
            return connections.Any(c => c.LocalEndPoint.Port == port &&
                                        c.State == TcpState.Established);
        }
        catch { return false; }
    }
}