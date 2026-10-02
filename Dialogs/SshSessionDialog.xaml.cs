using Microsoft.UI.Xaml;
using ScarpaConnectionManager.Models;
using scarpa_connection_manager_win.Controls;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class SshSessionWindow : Window
{
    private TerminalControl _termControl;

    public SshSessionWindow(ServerConfig cfg, string? logPath)
    {
        this.InitializeComponent();
        this.Title = $"{cfg.Name} - SSH Terminal";

        // Set a nice default size for the terminal window
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1050, 750));
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets\\scarpa_icon.ico");
        appWindow.SetIcon(iconPath);


        // Create the terminal control programmatically
        _termControl = new TerminalControl(cfg, logPath);

        // Add the terminal directly into our Grid
        RootGrid.Children.Add(_termControl);

        // Safely close the SSH connection when the window is closed
        this.Closed += SshSessionWindow_Closed;
    }

    private void SshSessionWindow_Closed(object sender, WindowEventArgs args)
    {
        _termControl.CloseSession();
    }
}
