using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Renci.SshNet;
using ScarpaConnectionManager.Models;

namespace scarpa_connection_manager_win.Dialogs;

public class RemoteFileItem
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string Icon => IsDirectory ? "\uE8D5" : "\uE7C3";
    public Brush IconColor => IsDirectory
        ? new SolidColorBrush(Microsoft.UI.Colors.Gold)
        : new SolidColorBrush(Microsoft.UI.Colors.LightGray);
}

// Explicitly inherit from Window to fix CS0263
public sealed partial class SftpWindow : Window
{
    private SftpClient? _sftpClient;
    private ObservableCollection<RemoteFileItem> _remoteFiles = new();

    public SftpWindow(ServerConfig cfg)
    {
        this.InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));

        this.Title = $"{cfg.Name} - SFTP File Manager";
        RemoteFileList.ItemsSource = _remoteFiles;

        this.Closed += SftpWindow_Closed;

        _ = ConnectAndListFilesAsync(cfg);
    }

    private async Task ConnectAndListFilesAsync(ServerConfig cfg)
    {
        await Task.Run(() =>
        {
            try
            {
                var authMethod = new PasswordAuthenticationMethod(cfg.User, cfg.Password ?? "");
                var connectionInfo = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, authMethod);

                _sftpClient = new SftpClient(connectionInfo);
                _sftpClient.Connect();

                RefreshRemoteDirectory(_sftpClient.WorkingDirectory);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RemotePathText.Text = $"Connection Failed: {ex.Message}";
                    RemotePathText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                });
            }
        });
    }

    private void RefreshRemoteDirectory(string path)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        var files = _sftpClient.ListDirectory(path);

        DispatcherQueue.TryEnqueue(() =>
        {
            _remoteFiles.Clear();
            RemotePathText.Text = path;

            foreach (var file in files)
            {
                if (file.Name == ".") continue;

                _remoteFiles.Add(new RemoteFileItem
                {
                    Name = file.Name,
                    IsDirectory = file.IsDirectory
                });
            }
        });
    }

    private void SftpWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_sftpClient != null && _sftpClient.IsConnected)
        {
            _sftpClient.Disconnect();
        }
        _sftpClient?.Dispose();
    }
}