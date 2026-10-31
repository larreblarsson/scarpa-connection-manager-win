using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class AndroidMirrorWindow : Window
{
    private ObservableCollection<AndroidDevice> _devices = new();
    private Dictionary<string, AndroidDevice> _savedSettings = new();
    private readonly string _settingsFile;

    // Background watcher to detect USB unplug/plug automatically
    private DispatcherTimer _adbWatcherTimer;
    private string _lastAdbOutput = string.Empty;
    private Dictionary<string, DateTime> _lastReconnectAttempts = new();

    public AndroidMirrorWindow()
    {
        this.InitializeComponent();
        DeviceListView.ItemsSource = _devices;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsFile = Path.Combine(appData, "ScarpaConnectionManager", "android_devices.json");

        LoadSettings();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 750));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets\\scarpa_icon.ico"));

        this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += async (s, e) =>
            {
                await RefreshDevicesAsync(true);

                // Start silently checking for USB/Wi-Fi changes every 2 seconds
                _adbWatcherTimer = new DispatcherTimer();
                _adbWatcherTimer.Interval = TimeSpan.FromSeconds(2);
                _adbWatcherTimer.Tick += async (ts, te) => await RefreshDevicesAsync(false);
                _adbWatcherTimer.Start();
            };

            // Stop the timer when the window closes
            rootElement.Unloaded += (s, e) =>
            {
                _adbWatcherTimer?.Stop();
            };
        }
    }

    // --- SECURITY FIX: STRICT PATH HELPER ---
    // Safely resolves the absolute path to your bundled tools to prevent PATH hijacking
    private string GetSecureToolPath(string toolName)
    {
        string bundledPath = Path.Combine(AppContext.BaseDirectory, "Tools", "scrcpy", $"{toolName}.exe");
        return File.Exists(bundledPath) ? bundledPath : toolName; // Fallback to PATH only if missing
    }
    // ----------------------------------------

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                string json = File.ReadAllText(_settingsFile);
                _savedSettings = JsonSerializer.Deserialize<Dictionary<string, AndroidDevice>>(json) ?? new();
            }
        }
        catch { _savedSettings = new(); }
    }

    private void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_savedSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFile, json);
        }
        catch { }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(true);
    }

    private async Task RefreshDevicesAsync(bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            RefreshButton.IsEnabled = false;
            LoadingRing.IsActive = true;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetSecureToolPath("adb"),
                Arguments = "devices",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // --- AUTO-RECOVERY FOR DROPPED WI-FI CONNECTIONS ---
                bool attemptedRecovery = false;
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    // If a Wi-Fi device dropped offline (usually from unplugging the USB)
                    if (parts.Length >= 2 && parts[0].Contains(":") && parts[1] == "offline")
                    {
                        string ip = parts[0].Split(':')[0];

                        // Try to wake it up, but only once every 10 seconds to avoid background lag
                        if (!_lastReconnectAttempts.TryGetValue(ip, out DateTime lastAttempt) ||
                            (DateTime.Now - lastAttempt).TotalSeconds > 10)
                        {
                            _lastReconnectAttempts[ip] = DateTime.Now;

                            var psiConnect = new ProcessStartInfo
                            {
                                FileName = GetSecureToolPath("adb"),
                                Arguments = $"connect {ip}:5555",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            using (var pConnect = Process.Start(psiConnect)) { await pConnect?.WaitForExitAsync(); }
                            attemptedRecovery = true;
                        }
                    }
                }

                // If we attempted to wake a device, grab the fresh ADB list immediately
                if (attemptedRecovery)
                {
                    using var processFresh = Process.Start(psi);
                    if (processFresh != null)
                    {
                        output = await processFresh.StandardOutput.ReadToEndAsync();
                        await processFresh.WaitForExitAsync();
                    }
                }
                // ---------------------------------------------------

                if (forceRefresh || output != _lastAdbOutput)
                {
                    _lastAdbOutput = output;
                    _devices.Clear();
                    ParseAdbOutput(output);
                }
            }
        }
        catch (Exception ex)
        {
            if (forceRefresh)
                await ShowErrorDialog("ADB Error", $"Ensure 'adb' is installed and added to your system PATH.\n\nDetails: {ex.Message}");
        }
        finally
        {
            if (forceRefresh)
            {
                LoadingRing.IsActive = false;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task<string> GetDeviceIpAddressAsync(string deviceId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetSecureToolPath("adb"),
                Arguments = $"-s {deviceId} shell ip route",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                string[] parts = output.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "src" && i + 1 < parts.Length)
                    {
                        string ip = parts[i + 1];
                        if (ip.Split('.').Length == 4) return ip;
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private void ParseAdbOutput(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var parsedDevices = new List<(string Id, string State, bool IsTcpIp)>();

        // Pass 1: Gather all active ADB devices
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                parsedDevices.Add((parts[0], parts[1], parts[0].Contains(":")));
            }
        }

        // Pass 2: Build the UI list smartly
        foreach (var pd in parsedDevices)
        {
            // If this is a Wi-Fi connection, see if the USB cable for it is also plugged in
            if (pd.IsTcpIp)
            {
                string ipOnly = pd.Id.Split(':')[0];
                var parentDevice = _savedSettings.Values.FirstOrDefault(d => d.WifiIpAddress == ipOnly);

                if (parentDevice != null)
                {
                    // If the USB version is also currently connected, skip adding a duplicate row
                    if (parsedDevices.Any(x => x.Id == parentDevice.Id && !x.IsTcpIp))
                    {
                        continue;
                    }
                }
            }

            AndroidDevice device = _savedSettings.ContainsKey(pd.Id)
                ? _savedSettings[pd.Id]
                : new AndroidDevice { Id = pd.Id };

            // If it's a Wi-Fi connection but we know its IP from a USB setup, inherit its name/settings
            if (pd.IsTcpIp && !_savedSettings.ContainsKey(pd.Id))
            {
                string ipOnly = pd.Id.Split(':')[0];
                var parentDevice = _savedSettings.Values.FirstOrDefault(d => d.WifiIpAddress == ipOnly);
                if (parentDevice != null)
                {
                    device.CustomName = parentDevice.CustomName;
                    device.VideoBitrate = parentDevice.VideoBitrate;
                    device.MaxResolution = parentDevice.MaxResolution;
                    device.TurnScreenOff = parentDevice.TurnScreenOff;
                    device.EnableAudio = parentDevice.EnableAudio;
                    device.WifiIpAddress = ipOnly;
                }
            }

            device.State = pd.State;
            device.ConnectionType = pd.IsTcpIp ? "Wi-Fi" : "USB";

            // If this is the USB device, check if its Wi-Fi counterpart is ALSO active right now
            if (!pd.IsTcpIp && !string.IsNullOrWhiteSpace(device.WifiIpAddress))
            {
                if (parsedDevices.Any(x => x.IsTcpIp && x.Id.StartsWith(device.WifiIpAddress + ":")))
                {
                    device.ConnectionType = "USB & Wi-Fi";
                }
            }

            _devices.Add(device);
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            AndroidDevice? device = null;
            foreach (var d in _devices)
            {
                if (d.Id == deviceId) { device = d; break; }
            }
            if (device == null || this.Content?.XamlRoot == null) return;

            if (string.IsNullOrWhiteSpace(device.WifiIpAddress) && device.IsOnline)
            {
                string detectedIp = await GetDeviceIpAddressAsync(device.Id);
                if (!string.IsNullOrWhiteSpace(detectedIp))
                {
                    device.WifiIpAddress = detectedIp;
                }
            }

            var nameBox = new TextBox { Text = device.CustomName, Header = "Device Name", Margin = new Thickness(0, 0, 0, 15) };
            var bitrateBox = new ComboBox { Header = "Video Bitrate", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 15) };
            bitrateBox.ItemsSource = new[] { "Default", "2 Mbps", "4 Mbps", "8 Mbps", "16 Mbps", "32 Mbps" };
            bitrateBox.SelectedItem = string.IsNullOrEmpty(device.VideoBitrate) ? "Default" : device.VideoBitrate;

            var resBox = new ComboBox { Header = "Max Resolution", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 15) };
            resBox.ItemsSource = new[] { "Default", "720", "1080", "1440" };
            resBox.SelectedItem = string.IsNullOrEmpty(device.MaxResolution) ? "Default" : device.MaxResolution;

            var screenOffCheck = new CheckBox { Content = "Turn device screen off while mirroring", IsChecked = device.TurnScreenOff, Margin = new Thickness(0, 0, 0, 5) };
            var audioCheck = new CheckBox { Content = "Enable Audio Relay", IsChecked = device.EnableAudio, Margin = new Thickness(0, 0, 0, 15) };
            var wirelessBtn = new Button { Content = "Setup Wireless Connection", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };

            if (!string.IsNullOrEmpty(device.ConnectionType) && device.ConnectionType.Contains("Wi-Fi"))
            {
                wirelessBtn.Visibility = Visibility.Collapsed;
            }

            var forgetBtn = new Button { Content = "Forget Device", HorizontalAlignment = HorizontalAlignment.Stretch };

            var contentPanel = new StackPanel { Width = 350 };
            contentPanel.Children.Add(nameBox);
            contentPanel.Children.Add(bitrateBox);
            contentPanel.Children.Add(resBox);
            contentPanel.Children.Add(screenOffCheck);
            contentPanel.Children.Add(audioCheck);
            contentPanel.Children.Add(wirelessBtn);
            contentPanel.Children.Add(forgetBtn);

            var dialog = new ContentDialog
            {
                Title = "Device Settings",
                Content = contentPanel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            wirelessBtn.Click += async (s, ev) =>
            {
                string ip = device.WifiIpAddress?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(ip))
                {
                    wirelessBtn.Content = "Could not auto-detect IP. Connect USB first.";
                    await Task.Delay(2000);
                    wirelessBtn.Content = "Setup Wireless Connection";
                    return;
                }

                wirelessBtn.IsEnabled = false;
                wirelessBtn.Content = "Configuring Port 5555...";

                try
                {
                    var psiTcp = new ProcessStartInfo
                    {
                        FileName = GetSecureToolPath("adb"),
                        Arguments = $"-s {device.Id} tcpip 5555",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p1 = Process.Start(psiTcp)) { await p1?.WaitForExitAsync(); }

                    wirelessBtn.Content = "Connecting over Wi-Fi...";
                    await Task.Delay(1500);

                    var psiConnect = new ProcessStartInfo
                    {
                        FileName = GetSecureToolPath("adb"),
                        Arguments = $"connect {ip}:5555",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p2 = Process.Start(psiConnect))
                    {
                        string outText = await p2.StandardOutput.ReadToEndAsync();
                        await p2.WaitForExitAsync();

                        if (outText.Contains("connected to") || outText.Contains("already connected"))
                        {
                            wirelessBtn.Content = "Success! You can unplug the USB.";
                            device.WifiIpAddress = ip;

                            // Save immediately and force a refresh so the main list updates instantly
                            _savedSettings[device.Id] = device;
                            SaveSettings();
                            _ = RefreshDevicesAsync(true);
                        }
                        else
                        {
                            wirelessBtn.Content = "Failed. Is the device on the same Wi-Fi?";
                        }
                    }
                }
                catch
                {
                    wirelessBtn.Content = "ADB Error! Check PATH.";
                }

                await Task.Delay(3500);
                wirelessBtn.Content = "Setup Wireless Connection";
                wirelessBtn.IsEnabled = true;
            };

            forgetBtn.Click += async (s, ev) =>
            {
                // 1. Find the correct Wi-Fi IP to disconnect BEFORE we delete the settings
                string ipToDisconnect = string.Empty;
                string ipOnly = string.Empty;

                if (device.Id.Contains(":"))
                {
                    ipToDisconnect = device.Id;
                    ipOnly = device.Id.Split(':')[0];
                }
                else if (!string.IsNullOrWhiteSpace(device.WifiIpAddress))
                {
                    ipToDisconnect = $"{device.WifiIpAddress}:5555";
                    ipOnly = device.WifiIpAddress;
                }

                // 2. Delete ALL saved settings linked to this device (both USB and Wi-Fi profiles)
                var keysToRemove = _savedSettings.Where(kvp =>
                    kvp.Key == device.Id ||
                    kvp.Key == ipToDisconnect ||
                    (!string.IsNullOrEmpty(ipOnly) && kvp.Value.WifiIpAddress == ipOnly)
                ).Select(kvp => kvp.Key).ToList();

                foreach (var key in keysToRemove)
                {
                    _savedSettings.Remove(key);
                }

                // Instantly wipe the name and IP from memory so the UI updates cleanly
                device.CustomName = string.Empty;
                device.WifiIpAddress = string.Empty;

                SaveSettings();

                // 3. Explicitly tell ADB to sever the Wi-Fi connection and revert to USB mode
                if (!string.IsNullOrEmpty(ipToDisconnect))
                {
                    try
                    {
                        // FIRST: Tell the Android device to close the Wi-Fi port and return to normal USB mode
                        var psiUsb = new ProcessStartInfo
                        {
                            FileName = GetSecureToolPath("adb"),
                            Arguments = $"-s {device.Id} usb",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var pUsb = Process.Start(psiUsb)) { await pUsb?.WaitForExitAsync(); }

                        // SECOND: Now that the phone knows to switch back, sever the local network route
                        var psiDisconnect = new ProcessStartInfo
                        {
                            FileName = GetSecureToolPath("adb"),
                            Arguments = $"disconnect {ipToDisconnect}",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var pDisc = Process.Start(psiDisconnect)) { await pDisc?.WaitForExitAsync(); }
                    }
                    catch { }
                }

                dialog.Hide();
                _ = RefreshDevicesAsync(true);
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                device.CustomName = nameBox.Text.Trim();
                device.VideoBitrate = bitrateBox.SelectedItem?.ToString() ?? "Default";
                device.MaxResolution = resBox.SelectedItem?.ToString() ?? "Default";
                device.TurnScreenOff = screenOffCheck.IsChecked == true;
                device.EnableAudio = audioCheck.IsChecked == true;

                _savedSettings[device.Id] = device;
                SaveSettings();
                _ = RefreshDevicesAsync(true);
            }
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string targetAdbId)
        {
            try
            {
                AndroidDevice? deviceSettings = null;

                // 1. Try to find the exact ID (USB Serial)
                if (_savedSettings.TryGetValue(targetAdbId, out var existing))
                {
                    deviceSettings = existing;
                }
                // 2. If this is a Wi-Fi target, inherit the settings from its USB parent profile
                else if (targetAdbId.Contains(":"))
                {
                    string ipOnly = targetAdbId.Split(':')[0];
                    deviceSettings = _savedSettings.Values.FirstOrDefault(d => d.WifiIpAddress == ipOnly);
                }

                // 3. Fallback
                if (deviceSettings == null)
                {
                    deviceSettings = new AndroidDevice { Id = targetAdbId };
                }

                if (!deviceSettings.HasSeenShortcutsPrompt)
                {
                    int sdkVersion = 99;
                    try
                    {
                        var psiAdb = new ProcessStartInfo
                        {
                            FileName = GetSecureToolPath("adb"),
                            Arguments = $"-s {targetAdbId} shell getprop ro.build.version.sdk",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var process = Process.Start(psiAdb);
                        if (process != null)
                        {
                            string output = await process.StandardOutput.ReadToEndAsync();
                            if (int.TryParse(output.Trim(), out int parsedSdk)) sdkVersion = parsedSdk;
                        }
                    }
                    catch { }

                    if (sdkVersion <= 26 && this.Content?.XamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Hardware Buttons Detected",
                            Content = "This appears to be an older device with physical navigation buttons.\n\nSince you won't see a software navigation bar on the screen, use these mouse and keyboard shortcuts instead:\n\n• HOME: Middle Mouse Click (Scroll Wheel)\n• BACK: Right Mouse Click\n• RECENT APPS: Alt + S (or Alt + W)\n• POWER (Wake/Sleep): Alt + P",
                            PrimaryButtonText = "Got it! Launch",
                            XamlRoot = this.Content.XamlRoot
                        };
                        await dialog.ShowAsync();

                        deviceSettings.HasSeenShortcutsPrompt = true;
                        if (_savedSettings.ContainsKey(deviceSettings.Id))
                        {
                            _savedSettings[deviceSettings.Id] = deviceSettings;
                            SaveSettings();
                        }
                    }
                }

                string windowTitle = !string.IsNullOrWhiteSpace(deviceSettings.CustomName)
                    ? deviceSettings.CustomName
                    : $"Android Mirror - {targetAdbId}";

                string args = $"-s {targetAdbId} --window-title \"{windowTitle}\"";

                if (deviceSettings.VideoBitrate != "Default")
                {
                    string bps = deviceSettings.VideoBitrate.Split(' ')[0];
                    args += $" -b {bps}M";
                }

                if (deviceSettings.MaxResolution != "Default")
                {
                    args += $" -m {deviceSettings.MaxResolution}";
                }

                if (deviceSettings.TurnScreenOff)
                {
                    args += " --turn-screen-off";
                }

                if (!deviceSettings.EnableAudio)
                {
                    args += " --no-audio";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = GetSecureToolPath("scrcpy"),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Launch scrcpy without the destructive exit event
                var scrcpyProcess = new Process { StartInfo = psi };
                scrcpyProcess.Start();
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Scrcpy Error", $"Ensure 'scrcpy' is installed and in your system PATH.\n\nDetails: {ex.Message}");
            }
        }
    }

    private async Task ShowErrorDialog(string title, string content)
    {
        if (this.Content?.XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}