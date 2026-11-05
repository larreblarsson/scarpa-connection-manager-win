using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using scarpa_connection_manager_win.Controls;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class SshSessionWindow : Window
{
    private TerminalControl? _termControl;
    private IDisposable? _jumpTunnel;
    private readonly ServerConfig _initialConfig;
    private readonly string? _logPath;
    private readonly List<ServerConfig>? _allServers;

    public SshSessionWindow(ServerConfig cfg, string? logPath, List<ServerConfig>? allServers = null, IDisposable? jumpTunnel = null)
    {
        this.InitializeComponent();
        this.Title = $"{cfg.Name} - SSH Terminal (Connecting...)";

        _initialConfig = cfg;
        _logPath = logPath;
        _allServers = allServers;
        _jumpTunnel = jumpTunnel;

        // Set default window size and show instantly
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1050, 750));

        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets\\scarpa_icon.ico");
        appWindow.SetIcon(iconPath);

        this.Closed += SshSessionWindow_Closed;

        // Run session/tunnel preparation in background without blocking UI
        _ = InitializeSessionAsync();
    }

    private async Task InitializeSessionAsync()
    {
        ServerConfig targetConfig = _initialConfig;

        if (_jumpTunnel == null && _allServers != null)
        {
            try
            {
                // Deconstruct tuple directly to avoid property naming issues
                var (routedConfig, tunnel) = await JumpServerService.PrepareConnectionAsync(_initialConfig, _allServers);
                targetConfig = routedConfig;
                _jumpTunnel = tunnel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tunnel preparation failed: {ex.Message}");
            }
        }

        this.Title = $"{_initialConfig.Name} - SSH Terminal";

        _termControl = new TerminalControl(targetConfig, _logPath);
        RootGrid.Children.Add(_termControl);
    }

    private void SshSessionWindow_Closed(object sender, WindowEventArgs args)
    {
        _termControl?.CloseSession();
        _jumpTunnel?.Dispose();
    }
}