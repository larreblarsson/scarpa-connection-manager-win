using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using Renci.SshNet;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;

namespace scarpa_connection_manager_win.Controls;

public sealed partial class TerminalControl : UserControl
{
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

    // Automation tracking
    private string? _currentExpect;
    private TaskCompletionSource<bool>? _expectTcs;
    private StringBuilder _loginBuffer = new();

    public TerminalControl(ServerConfig cfg, string? logPath, bool isSftp = false)
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

        _ = InitializeTerminalAsync(cfg);
    }

    private string ApplyLogSubstitutions(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var now = DateTime.Now;
        return input.Replace("%H", _config.Host ?? "").Replace("%S", _config.Name ?? "")
            .Replace("%Y", now.ToString("yyyy")).Replace("%M", now.ToString("MM"))
            .Replace("%D", now.ToString("dd")).Replace("%h", now.ToString("HH"))
            .Replace("%m", now.ToString("mm")).Replace("%s", now.ToString("ss"))
            .Replace("\\n", Environment.NewLine).Replace("\\r", "\r").Replace("\\t", "\t");
    }

    private void WriteToLog(string rawText)
    {
        if (_logWriter == null) return;
        try
        {
            string cleanText = Regex.Replace(rawText, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
            cleanText = Regex.Replace(cleanText, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

            string customPrefix = (_config.AppendDataToLog && !string.IsNullOrEmpty(_config.LogEachLineString))
                ? ApplyLogSubstitutions(_config.LogEachLineString) : "";

            if (!_logTimestamps && string.IsNullOrEmpty(customPrefix))
            {
                _logWriter.Write(cleanText);
                return;
            }

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

    private async Task InitializeTerminalAsync(ServerConfig cfg)
    {
        await TerminalView.EnsureCoreWebView2Async();
        TerminalView.CoreWebView2.SetVirtualHostNameToFolderMapping("terminal.local", AppContext.BaseDirectory, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

        TerminalView.WebMessageReceived += (s, e) =>
        {
            string input = e.TryGetWebMessageAsString();
            if (_isSftp)
            {
                if (_sftpInputWriter != null) { _sftpInputWriter.Write(input); _sftpInputWriter.Flush(); }
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

        int scrollback = _config.TermScrollback > 0 ? _config.TermScrollback : 10000;
        string bg = string.IsNullOrWhiteSpace(_config.TermBackground) ? "#101010" : _config.TermBackground;
        string fg = string.IsNullOrWhiteSpace(_config.TermForeground) ? "#D0D0D0" : _config.TermForeground;

        string initScript = $@"
            try {{
                let targetTerm = typeof term !== 'undefined' ? term : (typeof window.term !== 'undefined' ? window.term : null);
                if (targetTerm) {{
                    targetTerm.options.scrollback = {scrollback};
                    targetTerm.options.theme = {{ background: '{bg}', foreground: '{fg}' }};
                }}
            }} catch(e) {{ }}
        ";

        _ = TerminalView.CoreWebView2.ExecuteScriptAsync(initScript);
    }

    private void StartSshSession(ServerConfig cfg)
    {
        Task.Run(() =>
        {
            try
            {
                var authMethods = new List<AuthenticationMethod>();

                // 1. Key File Auth
                if (cfg.AuthMethod == "key_file" && !string.IsNullOrWhiteSpace(cfg.KeyFile))
                {
                    if (File.Exists(cfg.KeyFile))
                        authMethods.Add(new PrivateKeyAuthenticationMethod(cfg.User, new PrivateKeyFile(cfg.KeyFile)));
                    else
                    {
                        SendToTerminal($"\r\n\x1b[31m[Error] Key file not found: {cfg.KeyFile}\x1b[0m\r\n");
                        return;
                    }
                }
                else
                {
                    // 2. Password & Keyboard Interactive Auth
                    string pass = cfg.Password ?? "";

                    authMethods.Add(new PasswordAuthenticationMethod(cfg.User, pass));

                    var kbdAuth = new KeyboardInteractiveAuthenticationMethod(cfg.User);
                    kbdAuth.AuthenticationPrompt += (sender, e) =>
                    {
                        foreach (var prompt in e.Prompts)
                        {
                            if (string.IsNullOrEmpty(pass))
                                SendToTerminal($"\r\n\x1b[36m[Server Prompt]: {prompt.Request}\x1b[0m\r\n");

                            prompt.Response = pass;
                        }
                    };
                    authMethods.Add(kbdAuth);
                }

                var connectionInfo = new ConnectionInfo(cfg.Host, cfg.Port > 0 ? cfg.Port : 22, cfg.User, authMethods.ToArray());
                _sshClient = new SshClient(connectionInfo);
                _sshClient.Connect();

                _shellStream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

                StartAntiIdle(cfg);

                // Launch the reader and automation concurrently
                _ = ReadStreamAsync();
                _ = RunPostLoginAutomationAsync(cfg);
            }
            catch (Exception ex)
            {
                SendToTerminal($"\r\n\x1b[31mConnection failed: {ex.Message}\x1b[0m\r\n");
            }
        });
    }

    private async Task RunPostLoginAutomationAsync(ServerConfig cfg)
    {
        // 1. Execute Login Actions (Expect/Send) First
        if (cfg.LoginActions != null && cfg.LoginActions.Count > 0)
        {
            foreach (var step in cfg.LoginActions)
            {
                if (string.IsNullOrEmpty(step.Expect)) continue;

                _expectTcs = new TaskCompletionSource<bool>();
                _currentExpect = step.Expect;

                // Check if the prompt already arrived BEFORE we even started waiting
                if (_loginBuffer.ToString().Contains(_currentExpect))
                {
                    _currentExpect = null;
                    _loginBuffer.Clear(); // <-- FIX: Wipe immediately
                    _expectTcs.TrySetResult(true);
                }

                // Wait for the string to appear OR the timeout to expire
                var timeoutTask = Task.Delay(step.Timeout > 0 ? step.Timeout * 1000 : 5000);
                var completedTask = await Task.WhenAny(_expectTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    SendToTerminal($"\r\n\x1b[33m[Login Action] Timeout ({step.Timeout}s) waiting for '{step.Expect}'. Skipping step.\x1b[0m\r\n");

                    _currentExpect = null;
                    _loginBuffer.Clear(); // <-- FIX: Clear stale data on timeout

                    // Send a simulated "Enter" keypress (\r) to the server to force a fresh prompt line
                    if (_shellStream != null && _shellStream.CanWrite)
                    {
                        _shellStream.Write(new byte[] { 13 }, 0, 1);
                        _shellStream.Flush();
                    }
                    continue;
                }

                // Match found! Inject the Send command.
                string toSend = (step.Send ?? "").Replace("\\n", "\n").Replace("\\r", "\r");
                if (!toSend.EndsWith("\n") && !toSend.EndsWith("\r")) toSend += "\r"; // Auto-append enter

                if (_shellStream != null && _shellStream.CanWrite)
                {
                    var sendBytes = Encoding.UTF8.GetBytes(toSend);
                    _shellStream.Write(sendBytes, 0, sendBytes.Length);
                    _shellStream.Flush();
                }

                // Wait slightly before looking for the NEXT string so the server has time to receive the command
                await Task.Delay(250);

                // DELETED the old _loginBuffer.Clear() that was causing the race condition!
            }

            _currentExpect = null;
            _loginBuffer.Clear();
        }

        // 2. Execute Startup Command File AFTER the login sequence finishes
        if (cfg.StartupCmdEnabled && !string.IsNullOrWhiteSpace(cfg.StartupCmdPath))
        {
            try
            {
                if (File.Exists(cfg.StartupCmdPath))
                {
                    // Give the shell a moment to initialize only if we skipped the login actions
                    if (cfg.LoginActions == null || cfg.LoginActions.Count == 0) await Task.Delay(1000);

                    string script = await File.ReadAllTextAsync(cfg.StartupCmdPath);
                    script = script.Replace("\r\n", "\n").Replace("\r", "\n");

                    if (_shellStream != null && _shellStream.CanWrite)
                    {
                        var bytes = Encoding.UTF8.GetBytes(script);
                        _shellStream.Write(bytes, 0, bytes.Length);
                        _shellStream.Flush();
                    }
                }
                else
                {
                    SendToTerminal($"\r\n\x1b[33m[Warning] Startup command file not found: {cfg.StartupCmdPath}\x1b[0m\r\n");
                }
            }
            catch (Exception ex)
            {
                SendToTerminal($"\r\n\x1b[31m[Error] Failed to execute startup file: {ex.Message}\x1b[0m\r\n");
            }
        }
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
            catch (Exception ex) { SendToTerminal($"\r\n\x1b[31mSFTP Launch failed: {ex.Message}\x1b[0m\r\n"); }
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
                SendToTerminal(new string(buffer, 0, charsRead));
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

                // ALWAYS strip invisible characters and accumulate into the rolling buffer
                string cleanText = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", "");
                cleanText = Regex.Replace(cleanText, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

                _loginBuffer.Append(cleanText);
                if (_loginBuffer.Length > 10000) _loginBuffer.Remove(0, 5000);

                // Check if we matched the active expect string
                if (_currentExpect != null && _loginBuffer.ToString().Contains(_currentExpect))
                {
                    _currentExpect = null;
                    _loginBuffer.Clear(); // <-- FIX: Wipe the matched text immediately so we don't double-match
                    _expectTcs?.TrySetResult(true);
                }
            }
            SendToTerminal("\r\n\x1b[33mSession disconnected.\x1b[0m\r\n");
        }
        catch { }
    }

    private void SendToTerminal(string text)
    {
        string base64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        DispatcherQueue.TryEnqueue(() => { _ = TerminalView.CoreWebView2.ExecuteScriptAsync($"window.writeToTerminal('{base64Data}');"); });
    }

    public void CloseSession()
    {
        try
        {
            _antiIdleCts.Cancel();
            if (_logWriter != null && _config.AppendDataToLog && !string.IsNullOrWhiteSpace(_config.LogDisconnectString))
            {
                if (!_isNewLine) _logWriter.Write(Environment.NewLine);
                _logWriter.Write(ApplyLogSubstitutions(_config.LogDisconnectString) + Environment.NewLine);
            }

            _logWriter?.Dispose();
            _shellStream?.Dispose();
            if (_sshClient != null && _sshClient.IsConnected) _sshClient.Disconnect();
            _sshClient?.Dispose();

            _sftpInputWriter?.Close();
            if (_sftpProcess != null && !_sftpProcess.HasExited) _sftpProcess.Kill();
        }
        catch { }
    }
}