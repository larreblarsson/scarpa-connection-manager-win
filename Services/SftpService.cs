using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Services;

/// <summary>Thin SSH.NET wrapper backing the built-in dual-pane SFTP browser.</summary>
public sealed class SftpService : IDisposable
{
    private SftpClient? _client;

    public bool IsConnected => _client?.IsConnected == true;

    // Update the method signature to accept DispatcherQueue and XamlRoot
    public void Connect(ServerConfig cfg, string? password, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        ConnectionInfo info;
        if (cfg.AuthMethod == "key_file" && !string.IsNullOrWhiteSpace(cfg.KeyFile))
        {
            var keyFile = string.IsNullOrEmpty(password)
                ? new PrivateKeyFile(cfg.KeyFile)
                : new PrivateKeyFile(cfg.KeyFile, password);
            info = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, new PrivateKeyAuthenticationMethod(cfg.User, keyFile));
        }
        else
        {
            var passAuth = new PasswordAuthenticationMethod(cfg.User, password ?? "");
            var kbdAuth = new KeyboardInteractiveAuthenticationMethod(cfg.User);
            kbdAuth.AuthenticationPrompt += (sender, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password ?? "";
            };

            info = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, passAuth, kbdAuth);
        }

        _client = new SftpClient(info);

        // ADD THIS HOOK: Validates the key before allowing the connection to complete
        _client.HostKeyReceived += (sender, e) =>
        {
            KnownHostsValidator.HandleHostKey(e, cfg.Host, dispatcher, xamlRoot);
        };

        _client.Connect();
    }

    public string WorkingDirectory => _client?.WorkingDirectory ?? "/";

    public IEnumerable<ISftpFile> List(string path) =>
        (_client ?? throw new InvalidOperationException("Not connected"))
        .ListDirectory(path)
        .Where(f => f.Name != ".");

    public void Download(string remote, string local, Action<ulong>? progress = null)
    {
        using var fs = File.Create(local);
        _client!.DownloadFile(remote, fs, progress);
    }

    public void Upload(string local, string remote, Action<ulong>? progress = null)
    {
        using var fs = File.OpenRead(local);
        _client!.UploadFile(fs, remote, true, progress);
    }

    public void CreateDirectory(string remote) => _client!.CreateDirectory(remote);
    public void Rename(string from, string to) => _client!.RenameFile(from, to);

    public void Delete(string remote, bool isDirectory)
    {
        if (!isDirectory) { _client!.DeleteFile(remote); return; }
        foreach (var entry in _client!.ListDirectory(remote))
        {
            if (entry.Name is "." or "..") continue;
            Delete(entry.FullName, entry.IsDirectory);
        }
        _client.DeleteDirectory(remote);
    }

    public void Dispose()
    {
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { }
        _client?.Dispose();
        _client = null;
    }
}