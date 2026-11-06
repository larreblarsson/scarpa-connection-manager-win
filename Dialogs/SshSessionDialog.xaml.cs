using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using scarpa_connection_manager_win.Controls;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
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

        _initialConfig = cfg;
        _logPath = logPath;
        _allServers = allServers;
        _jumpTunnel = jumpTunnel;

        // --- CUSTOM TITLE BAR SETUP ---
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleText.Text = $"{_initialConfig.Name} - SSH Terminal (Connecting...)";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(1050, 750));
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets\\scarpa_icon.ico");
        appWindow.SetIcon(iconPath);

        // Calculate exact width needed to prevent drawing over the Minimize/Maximize/Close buttons
        RightPaddingGrid.Width = appWindow.TitleBar.RightInset;

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
                var (routedConfig, tunnel) = await JumpServerService.PrepareConnectionAsync(_initialConfig, _allServers);
                targetConfig = routedConfig;
                _jumpTunnel = tunnel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tunnel preparation failed: {ex.Message}");
            }
        }

        AppTitleText.Text = $"{_initialConfig.Name} - SSH Terminal";

        _termControl = new TerminalControl(targetConfig, _logPath);
        TerminalGrid.Children.Add(_termControl);
    }

    private async void EditConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var folders = _allServers?.Select(s => s.Folder).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList() ?? new List<string>();

        var dialog = new ServerDialog(_initialConfig, folders, _initialConfig.Folder);

        if (await dialog.ShowModalAsync() && dialog.Saved)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(dialog.Config);
            var updatedCfg = System.Text.Json.JsonSerializer.Deserialize<ServerConfig>(json);

            if (updatedCfg != null)
            {
                foreach (var prop in typeof(ServerConfig).GetProperties())
                {
                    if (prop.CanWrite) prop.SetValue(_initialConfig, prop.GetValue(updatedCfg));
                }
            }

            AppTitleText.Text = $"{_initialConfig.Name} - SSH Terminal";

            if (_termControl != null)
            {
                await _termControl.ApplyLiveAppearanceAsync(_initialConfig);
            }
        }
    }

    private void SshSessionWindow_Closed(object sender, WindowEventArgs args)
    {
        _termControl?.CloseSession();
        _jumpTunnel?.Dispose();
    }
}