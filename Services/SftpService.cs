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

    private volatile bool _isDisposing = false;

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
        _isDisposing = true; // Tell all active transfers that we are shutting down
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public void Download(string remote, string local, Action<ulong>? progress = null)
    {
        var client = _client;
        if (client == null || _isDisposing) return;

        try
        {
            using var fs = File.Create(local);
            client.DownloadFile(remote, fs, progress);
        }
        catch (Exception)
        {
            try { if (_isDisposing || !client.IsConnected) return; } catch { return; }
            throw; // Re-throw only if it's a genuine error while the app is fully alive
        }
    }

    public void Upload(string local, string remote, Action<ulong>? progress = null)
    {
        var client = _client;
        if (client == null || _isDisposing) return;

        try
        {
            using var fs = File.OpenRead(local);
            client.UploadFile(fs, remote, true, progress);
        }
        catch (Exception)
        {
            try { if (_isDisposing || !client.IsConnected) return; } catch { return; }
            throw;
        }
    }

    public void ResumeDownload(string remotePath, string localPath, long localSize, Action<ulong>? progress = null)
    {
        var client = _client;
        if (client == null || _isDisposing) return;

        try
        {
            using var localStream = new System.IO.FileStream(localPath, System.IO.FileMode.Append, System.IO.FileAccess.Write);
            using var remoteStream = client.OpenRead(remotePath);
            remoteStream.Seek(localSize, System.IO.SeekOrigin.Begin);

            // Read the stream in 80KB chunks so we can report progress to the UI
            byte[] buffer = new byte[81920];
            int read;
            ulong totalRead = 0;

            while ((read = remoteStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                localStream.Write(buffer, 0, read);
                totalRead += (ulong)read;
                progress?.Invoke(totalRead); // Fire the callback
            }
        }
        catch (Exception)
        {
            try { if (_isDisposing || !client.IsConnected) return; } catch { return; }
            throw;
        }
    }

    public void ResumeUpload(string localPath, string remotePath, long remoteSize, Action<ulong>? progress = null)
    {
        var client = _client;
        if (client == null || _isDisposing) return;

        try
        {
            using var localStream = new System.IO.FileStream(localPath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            localStream.Seek(remoteSize, System.IO.SeekOrigin.Begin);

            using var remoteStream = client.Open(remotePath, System.IO.FileMode.Append, System.IO.FileAccess.Write);

            byte[] buffer = new byte[81920];
            int read;
            ulong totalRead = 0;

            while ((read = localStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                remoteStream.Write(buffer, 0, read);
                totalRead += (ulong)read;
                progress?.Invoke(totalRead); // Fire the callback
            }
        }
        catch (Exception)
        {
            try { if (_isDisposing || !client.IsConnected) return; } catch { return; }
            throw;
        }
    }
}