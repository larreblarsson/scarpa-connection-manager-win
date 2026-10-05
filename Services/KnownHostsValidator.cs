using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Renci.SshNet.Common;

namespace ScarpaConnectionManager.Services;

public static class KnownHostsValidator
{
    public static void HandleHostKey(HostKeyEventArgs e, string host, DispatcherQueue dispatcher, Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        string knownHostsFile = Path.Combine(sshDir, "known_hosts");
        string keyStr = Convert.ToBase64String(e.HostKey);
        string keyType = e.HostKeyName;

        // 1. Check if we already trust this server
        if (File.Exists(knownHostsFile))
        {
            var lines = File.ReadAllLines(knownHostsFile);
            foreach (var line in lines)
            {
                if (line.StartsWith(host + " ") && line.Contains(keyStr))
                {
                    e.CanTrust = true;
                    return; // Key matches perfectly, proceed instantly!
                }
            }
        }

        // 2. If unknown or mismatched, we must ask the user
        bool isTrusted = false;
        using var syncEvent = new ManualResetEventSlim(false);

        dispatcher.TryEnqueue(async () =>
        {
            try
            {
                string fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLower();

                var dialog = new ContentDialog
                {
                    Title = "Security Alert - Unknown Host",
                    Content = $"The authenticity of host '{host}' can't be established.\n\n" +
                              $"Key type: {keyType}\n" +
                              $"MD5 Fingerprint: {fingerprint}\n\n" +
                              "Are you sure you want to continue connecting and trust this host?",
                    PrimaryButtonText = "Trust and Connect",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    isTrusted = true;
                    Directory.CreateDirectory(sshDir);
                    File.AppendAllText(knownHostsFile, $"{host} {keyType} {keyStr}\n");
                }
            }
            finally
            {
                // Unfreeze the background connection thread
                syncEvent.Set();
            }
        });

        // Freeze the SSH.NET connection thread until the user clicks a button
        syncEvent.Wait();
        e.CanTrust = isTrusted;
    }
}