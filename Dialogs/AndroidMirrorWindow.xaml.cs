using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class AndroidMirrorWindow : Window
{
    private ObservableCollection<AndroidDevice> _devices = new();
    private Dictionary<string, AndroidDevice> _savedSettings = new();
    private readonly string _settingsFile;

    public AndroidMirrorWindow()
    {
        this.InitializeComponent();
        DeviceListView.ItemsSource = _devices;

        // Define path for saving Android settings locally
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsFile = Path.Combine(appData, "ScarpaConnectionManager", "android_devices.json");

        LoadSettings();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets\\scarpa_icon.ico"));

        this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += async (s, e) =>
            {
                await RefreshDevicesAsync();
            };
        }
    }

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
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        _devices.Clear();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb",
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
                ParseAdbOutput(output);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("ADB Error", $"Ensure 'adb' is installed and added to your system PATH.\n\nDetails: {ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ParseAdbOutput(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string id = parts[0];

                // Merge active ADB state with saved user settings
                AndroidDevice device = _savedSettings.ContainsKey(id)
                    ? _savedSettings[id]
                    : new AndroidDevice { Id = id };

                device.State = parts[1];
                device.ConnectionType = id.Contains(":") ? "TCP/IP" : "USB";

                _devices.Add(device);
            }
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            // Find the device in the current list
            AndroidDevice? device = null;
            foreach (var d in _devices)
            {
                if (d.Id == deviceId) { device = d; break; }
            }
            if (device == null || this.Content?.XamlRoot == null) return;

            // Build the UI Elements
            var nameBox = new TextBox { Text = device.CustomName, Header = "Device Name", Margin = new Thickness(0, 0, 0, 15) };

            var bitrateBox = new ComboBox { Header = "Video Bitrate", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 15) };
            bitrateBox.ItemsSource = new[] { "Default", "2 Mbps", "4 Mbps", "8 Mbps", "16 Mbps", "32 Mbps" };
            bitrateBox.SelectedItem = string.IsNullOrEmpty(device.VideoBitrate) ? "Default" : device.VideoBitrate;

            var resBox = new ComboBox { Header = "Max Resolution", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 15) };
            resBox.ItemsSource = new[] { "Default", "720", "1080", "1440" };
            resBox.SelectedItem = string.IsNullOrEmpty(device.MaxResolution) ? "Default" : device.MaxResolution;

            var ipBox = new TextBox { Text = device.WifiIpAddress, Header = "Wi-Fi IP Address", PlaceholderText = "e.g., 192.168.1.50", Margin = new Thickness(0, 0, 0, 15) };

            var screenOffCheck = new CheckBox { Content = "Turn device screen off while mirroring", IsChecked = device.TurnScreenOff, Margin = new Thickness(0, 0, 0, 5) };
            var audioCheck = new CheckBox { Content = "Enable Audio Relay", IsChecked = device.EnableAudio, Margin = new Thickness(0, 0, 0, 15) };

            var wirelessBtn = new Button { Content = "Setup Wireless Connection", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
            var forgetBtn = new Button { Content = "Forget Device", HorizontalAlignment = HorizontalAlignment.Stretch };

            var contentPanel = new StackPanel { Width = 350 };
            contentPanel.Children.Add(nameBox);
            contentPanel.Children.Add(bitrateBox);
            contentPanel.Children.Add(resBox);
            contentPanel.Children.Add(ipBox);
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

            // --- WIRELESS SETUP LOGIC ---
            wirelessBtn.Click += async (s, ev) =>
            {
                string ip = ipBox.Text.Trim();
                if (string.IsNullOrEmpty(ip))
                {
                    wirelessBtn.Content = "Please enter an IP address first!";
                    await Task.Delay(2000);
                    wirelessBtn.Content = "Setup Wireless Connection";
                    return;
                }

                wirelessBtn.IsEnabled = false;
                wirelessBtn.Content = "Configuring Port 5555...";

                try
                {
                    // 1. Restart ADB on the phone to listen on TCP/IP 5555
                    var psiTcp = new ProcessStartInfo
                    {
                        FileName = "adb",
                        Arguments = $"-s {device.Id} tcpip 5555",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p1 = Process.Start(psiTcp)) { await p1?.WaitForExitAsync(); }

                    wirelessBtn.Content = "Connecting over Wi-Fi...";
                    await Task.Delay(1500); // Give the Android ADB daemon a moment to restart

                    // 2. Connect to the IP address
                    var psiConnect = new ProcessStartInfo
                    {
                        FileName = "adb",
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
                            device.WifiIpAddress = ip; // Auto-save it to the object
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

                // Reset the button after a few seconds
                await Task.Delay(3500);
                wirelessBtn.Content = "Setup Wireless Connection";
                wirelessBtn.IsEnabled = true;
            };

            // Handle Forget Device logic
            forgetBtn.Click += (s, ev) =>
            {
                if (_savedSettings.ContainsKey(device.Id))
                {
                    _savedSettings.Remove(device.Id);
                    SaveSettings();
                }
                dialog.Hide();
                _ = RefreshDevicesAsync(); // Reload the list
            };

            var result = await dialog.ShowAsync();

            // Handle Save logic
            if (result == ContentDialogResult.Primary)
            {
                device.CustomName = nameBox.Text.Trim();
                device.VideoBitrate = bitrateBox.SelectedItem?.ToString() ?? "Default";
                device.MaxResolution = resBox.SelectedItem?.ToString() ?? "Default";
                device.WifiIpAddress = ipBox.Text.Trim();
                device.TurnScreenOff = screenOffCheck.IsChecked == true;
                device.EnableAudio = audioCheck.IsChecked == true;

                _savedSettings[device.Id] = device;
                SaveSettings();

                _ = RefreshDevicesAsync();
            }
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            try
            {
                // Retrieve settings if saved, otherwise create a new tracking instance
                if (!_savedSettings.TryGetValue(deviceId, out AndroidDevice? device))
                {
                    device = new AndroidDevice { Id = deviceId };
                }

                // --- START OLD DEVICE DETECTION ---
                if (!device.HasSeenShortcutsPrompt)
                {
                    int sdkVersion = 99; // Default to new
                    try
                    {
                        var psiAdb = new ProcessStartInfo
                        {
                            FileName = "adb",
                            Arguments = $"-s {deviceId} shell getprop ro.build.version.sdk",
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

                    // Android 8.0 (API 26) and older commonly used physical hardware buttons
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

                        // Save the flag so we don't nag them again next time they connect
                        device.HasSeenShortcutsPrompt = true;
                        _savedSettings[deviceId] = device;
                        SaveSettings();
                    }
                }
                // --- END OLD DEVICE DETECTION ---

                string windowTitle = !string.IsNullOrWhiteSpace(device.CustomName)
                    ? device.CustomName
                    : $"Android Mirror - {deviceId}";

                string args = $"-s {deviceId} --window-title \"{windowTitle}\"";

                // Apply Bitrate
                if (device.VideoBitrate != "Default")
                {
                    string bps = device.VideoBitrate.Split(' ')[0]; // extracts "8" from "8 Mbps"
                    args += $" -b {bps}M";
                }

                // Apply Resolution
                if (device.MaxResolution != "Default")
                {
                    args += $" -m {device.MaxResolution}";
                }

                // Apply Screen Off
                if (device.TurnScreenOff)
                {
                    args += " --turn-screen-off";
                }

                // Apply Audio (scrcpy v2.0+ forwards audio by default. We disable it if unchecked)
                if (!device.EnableAudio)
                {
                    args += " --no-audio";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "scrcpy",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
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