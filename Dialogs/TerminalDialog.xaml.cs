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
using System.Threading;
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

    private ServerConfig _config;
    private CancellationTokenSource _antiIdleCts = new();

    public TerminalDialog(ServerConfig cfg, string? logPath, bool isSftp = false)
    {
        this.InitializeComponent();
        _config = cfg;
        _isSftp = isSftp;

        if (cfg.LoggingEnabled && !string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                bool append = cfg.LogMode != "overwrite";
                _logWriter = new StreamWriter(logPath, append, Encoding.UTF8) { AutoFlush = true };
                _logTimestamps = cfg.LogTimestamps;

                // Log: At Connect
                if (cfg.AppendDataToLog && !string.IsNullOrWhiteSpace(cfg.LogConnectString))
                {
                    _logWriter.Write(ApplyLogSubstitutions(cfg.LogConnectString) + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
            }
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));

        Task.Run(async () =>
        {
            await Task.Delay(300);
            DispatcherQueue.TryEnqueue(() =>
            {
                this.Activate();
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            });
        });

        this.Closed += TerminalDialog_Closed;
        _ = InitializeTerminalAsync(cfg, logPath);
    }

    private string ApplyLogSubstitutions(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var now = DateTime.Now;
        return input
            .Replace("%H", _config.Host ?? "")
            .Replace("%S", _config.Name ?? "")
            .Replace("%Y", now.ToString("yyyy"))
            .Replace("%M", now.ToString("MM"))
            .Replace("%D", now.ToString("dd"))
            .Replace("%h", now.ToString("HH"))
            .Replace("%m", now.ToString("mm"))
            .Replace("%s", now.ToString("ss"))
            .Replace("\\n", Environment.NewLine)
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }

    private void WriteToLog(string rawText)
    {
        if (_logWriter == null) return;

        try
        {
            string cleanText = Regex.Replace(rawText, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
            cleanText = Regex.Replace(cleanText, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

            string customPrefix = (_config.AppendDataToLog && !string.IsNullOrEmpty(_config.LogEachLineString))
                ? ApplyLogSubstitutions(_config.LogEachLineString)
                : "";

            // If no line-by-line modifications are needed, take the fast path
            if (!_logTimestamps && string.IsNullOrEmpty(customPrefix))
            {
                _logWriter.Write(cleanText);
                return;
            }

            // Inject timestamps and/or custom prefix at the beginning of each new line
            var sb = new StringBuilder();
            foreach (char c in cleanText)
            {
                if (_isNewLine && c != '\r' && c != '\n')
                {
                    if (_logTimestamps) sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
                    if (!string.IsNullOrEmpty(customPrefix)) sb.Append(customPrefix);
                    _isNewLine = false;
                }
                sb.Append(c);

                if (c == '\n') _isNewLine = true;
            }
            _logWriter.Write(sb.ToString());
        }
        catch { }
    }

    private async Task InitializeTerminalAsync(ServerConfig cfg, string? logPath)
    {
        await TerminalView.EnsureCoreWebView2Async();
        TerminalView.CoreWebView2.SetVirtualHostNameToFolderMapping("terminal.local", AppContext.BaseDirectory, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

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

        TerminalView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            if (_isSftp) StartSftpSession(cfg);
            else StartSshSession(cfg);
        };

        TerminalView.CoreWebView2.Navigate("https://terminal.local/terminal.html");
    }

    private void TerminalView_NavigationCompleted(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        TerminalView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void StartSshSession(ServerConfig cfg)
    {
        Task.Run(() =>
        {
            try
            {
                var authMethod = new PasswordAuthenticationMethod(cfg.User, cfg.Password ?? "");
                var connectionInfo = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, authMethod);

                _sshClient = new SshClient(connectionInfo);
                _sshClient.Connect();

                _shellStream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

                StartAntiIdle(cfg);
                _ = ReadStreamAsync();
            }
            catch (Exception ex)
            {
                SendToTerminal($"\r\n\x1b[31mConnection failed: {ex.Message}\x1b[0m\r\n");
            }
        });
    }

    private void StartAntiIdle(ServerConfig cfg)
    {
        if (!cfg.AntiIdleEnabled || cfg.AntiIdleInterval <= 0) return;

        string payload = (cfg.AntiIdleString ?? "").Replace("\\r", "\r").Replace("\\n", "\n");
        var bytes = Encoding.UTF8.GetBytes(payload);

        Task.Run(async () =>
        {
            try
            {
                while (_sshClient != null && _sshClient.IsConnected && !_antiIdleCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(cfg.AntiIdleInterval * 1000, _antiIdleCts.Token);
                    if (_shellStream != null && _shellStream.CanWrite)
                    {
                        _shellStream.Write(bytes, 0, bytes.Length);
                        _shellStream.Flush();
                    }
                }
            }
            catch (TaskCanceledException) { }
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
        catch { }
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
            _antiIdleCts.Cancel();

            // Log: At Disconnect
            if (_logWriter != null && _config.AppendDataToLog && !string.IsNullOrWhiteSpace(_config.LogDisconnectString))
            {
                if (!_isNewLine) _logWriter.Write(Environment.NewLine);
                _logWriter.Write(ApplyLogSubstitutions(_config.LogDisconnectString) + Environment.NewLine);
            }

            _logWriter?.Dispose();
            _shellStream?.Dispose();
            if (_sshClient != null && _sshClient.IsConnected)
            {
                _sshClient.Disconnect();
            }
            _sshClient?.Dispose();

            _sftpInputWriter?.Close();
            if (_sftpProcess != null && !_sftpProcess.HasExited)
            {
                _sftpProcess.Kill();
            }
        }
        catch { }
    }
}