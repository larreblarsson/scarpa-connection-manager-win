using Microsoft.UI.Xaml;
using Renci.SshNet;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    private bool _isSftp;
    private Process? _sftpProcess;
    private StreamWriter? _sftpInputWriter;

    private StreamWriter? _logWriter;
    private bool _logTimestamps;
    private bool _isNewLine = true;

    public TerminalDialog(ServerConfig cfg, string? logPath, bool isSftp = false)
    {
        this.InitializeComponent();

        if (cfg.LoggingEnabled && !string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                // Default to append unless overwrite is explicitly set
                bool append = cfg.LogMode != "overwrite";
                _logWriter = new StreamWriter(logPath, append, Encoding.UTF8) { AutoFlush = true };
                _logTimestamps = cfg.LogTimestamps;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
            }
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _isSftp = isSftp;

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

    private void WriteToLog(string rawText)
    {
        if (_logWriter == null) return;

        try
        {
            // 1. Strip ANSI escape codes (colors, cursor movements)
            string cleanText = Regex.Replace(rawText, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");

            // 2. Strip non-printable control characters (like Bell, Backspace, Delete) 
            // We explicitly KEEP Tab (\x09), Line Feed (\x0A), and Carriage Return (\x0D)
            cleanText = Regex.Replace(cleanText, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

            if (!_logTimestamps)
            {
                _logWriter.Write(cleanText);
                return;
            }

            // Inject timestamps at the beginning of each new line
            var sb = new StringBuilder();
            foreach (char c in cleanText)
            {
                if (_isNewLine && c != '\r' && c != '\n')
                {
                    sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
                    _isNewLine = false;
                }
                sb.Append(c);

                if (c == '\n') _isNewLine = true;
            }
            _logWriter.Write(sb.ToString());
        }
        catch
        {
            // Ignore logging errors to prevent crashing the active terminal session
        }
    }

    private async Task InitializeTerminalAsync(ServerConfig cfg, string? logPath)
    {
        await TerminalView.EnsureCoreWebView2Async();

        TerminalView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local",
            AppContext.BaseDirectory,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

        // Branch keystroke routing based on the session type
        TerminalView.WebMessageReceived += (s, e) =>
        {
            string input = e.TryGetWebMessageAsString();
            if (_isSftp)
            {
                if (_sftpInputWriter != null)
                {
                    _sftpInputWriter.Write(input);
                    _sftpInputWriter.Flush();
                }
            }
            else
            {
                if (_shellStream != null && _shellStream.CanWrite)
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    _shellStream.Write(bytes, 0, bytes.Length);
                    _shellStream.Flush();
                }
            }
        };

        // Branch session startup based on the session type
        TerminalView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            if (_isSftp) StartSftpSession(cfg);
            else StartSshSession(cfg);
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

    private void StartSftpSession(ServerConfig cfg)
    {
        Task.Run(() =>
        {
            try
            {
                string args = ConnectionLauncher.BuildSftpArguments(cfg);

                var psi = new ProcessStartInfo
                {
                    FileName = "sftp",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };

                _sftpProcess = Process.Start(psi);
                if (_sftpProcess != null)
                {
                    _sftpInputWriter = _sftpProcess.StandardInput;

                    _ = ReadProcessStreamAsync(_sftpProcess.StandardOutput);
                    _ = ReadProcessStreamAsync(_sftpProcess.StandardError);
                }
            }
            catch (Exception ex)
            {
                SendToTerminal($"\r\n\x1b[31mSFTP Launch failed: {ex.Message}\x1b[0m\r\n");
            }
        });
    }

    private async Task ReadProcessStreamAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        try
        {
            while (true)
            {
                int charsRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (charsRead == 0) break;

                string text = new string(buffer, 0, charsRead);
                SendToTerminal(text);
            }
            SendToTerminal("\r\n\x1b[33mSession disconnected.\x1b[0m\r\n");
        }
        catch { }
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
                WriteToLog(text);
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
            _logWriter?.Dispose();
            _shellStream?.Dispose();
            if (_sshClient != null && _sshClient.IsConnected)
            {
                _sshClient.Disconnect();
            }
            _sshClient?.Dispose();

            // SFTP Cleanup
            _sftpInputWriter?.Close();
            if (_sftpProcess != null && !_sftpProcess.HasExited)
            {
                _sftpProcess.Kill();
            }
        }
        catch { }
    }
}