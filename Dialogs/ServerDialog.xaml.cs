using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class ServerDialog : Window
{
    // Win32 API to lock/unlock the parent window
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    public ServerConfig Config { get; private set; }
    public bool Saved { get; private set; } = false;

    private IntPtr _parentHwnd;
    private TaskCompletionSource<bool> _tcs;

    public ServerDialog(ServerConfig? existingConfig, IEnumerable<string> folders, IntPtr parentHwnd, string? defaultFolder = null)
    {
        this.InitializeComponent();
        _parentHwnd = parentHwnd;

        // Set the standalone window title and size
        this.Title = "Server Configuration";
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));

        foreach (var f in folders) FolderBox.Items.Add(f);

        if (existingConfig != null)
        {
            Config = existingConfig.Clone();
            NameBox.Text = Config.Name ?? "";
            HostBox.Text = Config.Host ?? "";
            PortBox.Text = Config.Port.ToString();
            FolderBox.Text = Config.Folder ?? "";
            UserBox.Text = Config.User ?? "";
            PassBox.Password = Config.Password ?? "";
            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item.Content?.ToString() == Config.AuthMethod) AuthMethodBox.SelectedItem = item;
            }
        }
        else
        {
            Config = new ServerConfig();
            FolderBox.Text = defaultFolder ?? "";
            AuthMethodBox.SelectedIndex = 0;
        }

        NavView.SelectedItem = NavView.MenuItems[0];
        this.Closed += ServerDialog_Closed;
    }

    public Task<bool> ShowModalAsync()
    {
        _tcs = new TaskCompletionSource<bool>();
        EnableWindow(_parentHwnd, false); // Lock main window
        this.Activate(); // Show this window
        return _tcs.Task;
    }

    private void ServerDialog_Closed(object sender, WindowEventArgs args)
    {
        EnableWindow(_parentHwnd, true); // Unlock main window
        _tcs.TrySetResult(Saved);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) { }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => this.Close();

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text)) return;

        Config.Name = NameBox.Text;
        Config.Host = HostBox.Text;
        if (int.TryParse(PortBox.Text, out int p)) Config.Port = p;
        Config.Folder = FolderBox.Text;
        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;
        if (AuthMethodBox.SelectedItem is ComboBoxItem item) Config.AuthMethod = item.Content?.ToString() ?? "password";

        Saved = true;
        this.Close();
    }
}