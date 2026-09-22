using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Renci.SshNet;
using ScarpaConnectionManager.Models;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class TerminalDialog : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private SshClient? _sshClient;
    private ShellStream? _shellStream;

    public TerminalDialog(ServerConfig cfg, string? logPath, bool isSftp = false)
    {
        this.InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));

        // Wait for the Main Window's double-click event to completely finish, then steal focus
        Task.Run(async () =>
        {
            await Task.Delay(300); // Increased delay slightly to be safe
            DispatcherQueue.TryEnqueue(() =>
            {
                this.Activate(); // <-- Moved this here!
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            });
        });

        this.Closed += TerminalDialog_Closed;

        _ = InitializeTerminalAsync(cfg, logPath);
    }

    private async Task InitializeTerminalAsync(ServerConfig cfg, string? logPath)
    {
        await TerminalView.EnsureCoreWebView2Async();

        TerminalView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local",
            AppContext.BaseDirectory,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

        TerminalView.WebMessageReceived += (s, e) =>
        {
            if (_shellStream != null && _shellStream.CanWrite)
            {
                string input = e.TryGetWebMessageAsString();
                var bytes = Encoding.UTF8.GetBytes(input);
                _shellStream.Write(bytes, 0, bytes.Length);
                _shellStream.Flush();
            }
        };

        TerminalView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            StartSshSession(cfg);
        };

        TerminalView.CoreWebView2.Navigate("https://terminal.local/terminal.html");
    }

    private void TerminalView_NavigationCompleted(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        // Force the WebView2 control to take keyboard focus once the HTML is fully loaded
        TerminalView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void StartSshSession(ServerConfig cfg)
    {
        Task.Run(() =>
        {
            try
            {
                // Fallback to empty string if password is null
                var authMethod = new PasswordAuthenticationMethod(cfg.User, cfg.Password ?? "");
                var connectionInfo = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, authMethod);

                _sshClient = new SshClient(connectionInfo);
                _sshClient.Connect();

                // Request a true PTY (Pseudo-Terminal) from the server (xterm, 80 cols, 24 rows)
                _shellStream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

                _ = ReadStreamAsync();
            }
            catch (Exception ex)
            {
                SendToTerminal($"\r\n\x1b[31mConnection failed: {ex.Message}\x1b[0m\r\n");
            }
        });
    }

    private async Task ReadStreamAsync()
    {
        var buffer = new byte[1024];
        try
        {
            while (_shellStream != null && _sshClient != null && _sshClient.IsConnected)
            {
                int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                SendToTerminal(text);
            }
            SendToTerminal("\r\n\x1b[33mSession disconnected.\x1b[0m\r\n");
        }
        catch { /* Stream closed cleanly */ }
    }

    private void SendToTerminal(string text)
    {
        string base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = TerminalView.CoreWebView2.ExecuteScriptAsync($"window.writeToTerminal('{base64Data}');");
        });
    }

    private void TerminalDialog_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            _shellStream?.Dispose();
            if (_sshClient != null && _sshClient.IsConnected)
            {
                _sshClient.Disconnect();
            }
            _sshClient?.Dispose();
        }
        catch { }
    }
}