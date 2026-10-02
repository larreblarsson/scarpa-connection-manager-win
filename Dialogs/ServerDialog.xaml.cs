using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class ServerDialog : Window
{
    public ObservableCollection<LoginActionStep> LoginActions { get; set; } = new();

    public ServerConfig Config { get; private set; }
    public bool Saved { get; private set; } = false;

    private bool _isColorUpdating = false;
    private TaskCompletionSource<bool>? _tcs;

    public ObservableCollection<PortForwardRule> PortForwardRules { get; set; } = new();

    public ServerDialog(ServerConfig? existingConfig, IEnumerable<string> folders, string? defaultFolder = null)
    {
        this.InitializeComponent();
        this.Title = existingConfig == null ? "Add Server" : $"Edit {existingConfig.Name}";

        // Center the window on the screen
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        int windowWidth = 850;
        int windowHeight = 700;

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var workArea = displayArea.WorkArea;
            var x = (workArea.Width - windowWidth) / 2;
            var y = (workArea.Height - windowHeight) / 2;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, windowWidth, windowHeight));
        }
        else
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, windowHeight));
        }

        NavView.SelectedItem = NavView.MenuItems[0];
        foreach (var f in folders) FolderBox.Items.Add(f);

        if (existingConfig != null)
        {
            Config = existingConfig.Clone();

            // General
            NameBox.Text = Config.Name ?? "";
            HostBox.Text = Config.Host ?? "";
            PortBox.Text = Config.Port.ToString();

            if (!string.IsNullOrEmpty(Config.Folder) && FolderBox.Items.Contains(Config.Folder))
                FolderBox.SelectedItem = Config.Folder;
            else
                FolderBox.Text = Config.Folder ?? "";

            UserBox.Text = Config.User ?? "";
            PassBox.Password = Config.Password ?? "";
            KeyFileBox.Text = Config.KeyFile ?? "";

            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item != null && item.Content?.ToString() == Config.AuthMethod)
                    AuthMethodBox.SelectedItem = item;
            }

            // Login Actions
            LoginActions.Clear();
            if (Config.LoginActions != null)
            {
                foreach (var action in Config.LoginActions)
                    LoginActions.Add(new LoginActionStep { Expect = action.Expect, Send = action.Send, Timeout = action.Timeout });
            }
            LoginActionList.ItemsSource = LoginActions;

            // Port Forwarding
            PortForwardRules.Clear();
            if (Config.PortForwardRules != null)
            {
                foreach (var rule in Config.PortForwardRules)
                {
                    PortForwardRules.Add(new PortForwardRule
                    {
                        Name = rule.Name,
                        Type = rule.Type,
                        SourcePort = rule.SourcePort,
                        DestinationHost = rule.DestinationHost,
                        DestinationPort = rule.DestinationPort
                    });
                }
            }
            PortForwardList.ItemsSource = PortForwardRules;

            // Terminal - Logging
            EnableLoggingSwitch.IsOn = Config.LoggingEnabled;
            LogPathTextBox.Text = Config.LogPath ?? "";
            if (Config.LogMode == "overwrite") OverwriteRadio.IsChecked = true;
            else AppendRadio.IsChecked = true;

            AppendDataCheck.IsChecked = Config.AppendDataToLog;
            LogConnectBox.Text = Config.LogConnectString ?? "";
            LogDisconnectBox.Text = Config.LogDisconnectString ?? "";
            LogEachLineBox.Text = Config.LogEachLineString ?? "";

            // Terminal - Anti-idle
            AntiIdleCheck.IsChecked = Config.AntiIdleEnabled;
            AntiIdleStringBox.Text = Config.AntiIdleString ?? "";
            AntiIdleIntervalBox.Value = Config.AntiIdleInterval > 0 ? Config.AntiIdleInterval : 10;

            // Terminal - Startup Command
            StartupCmdCheck.IsChecked = Config.StartupCmdEnabled;
            StartupCmdPathBox.Text = Config.StartupCmdPath ?? "";

            // Appearance
            string fontStr = Config.TermFont ?? "Consolas 16";
            int lastSpace = fontStr.LastIndexOf(' ');
            string fontName = lastSpace > 0 ? fontStr.Substring(0, lastSpace).Trim() : fontStr;
            double fontSize = 16;

            if (lastSpace > 0 && double.TryParse(fontStr.Substring(lastSpace + 1), out double parsedSize))
            {
                fontSize = parsedSize;
            }

            SetComboValue(TermFontBox, fontName);
            SetComboValue(TermFontSizeBox, fontSize.ToString());

            TermFgBox.Text = Config.TermForeground ?? "#000000";
            TermBgBox.Text = Config.TermBackground ?? "#FFFFDD";
            TermScrollbackBox.Value = Config.TermScrollback > 0 ? Config.TermScrollback : 10000;

            SyncSchemeDropdown(TermFgBox.Text, TermBgBox.Text);
            UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            string targetPalette = Config.TermPalette ?? "None";
            foreach (ComboBoxItem item in PaletteBox.Items)
            {
                if (item.Content?.ToString() == targetPalette)
                {
                    PaletteBox.SelectedItem = item;
                    break;
                }
            }

            // RDP
            RdpEnabledSwitch.IsOn = Config.RdpEnabled;
            RdpPortBox.Text = Config.RdpPort.ToString();

            foreach (ComboBoxItem item in RdpResBox.Items)
            {
                if (item != null && item.Content?.ToString() == Config.RdpResolution)
                    RdpResBox.SelectedItem = item;
            }
            if (RdpResBox.SelectedItem == null) RdpResBox.SelectedIndex = 0;

            RdpAudioCheck.IsChecked = Config.RdpAudio;
            RdpClipboardCheck.IsChecked = Config.RdpClipboard;
            RdpCertCheck.IsChecked = Config.RdpIgnoreCert;
            RdpDriveCheck.IsChecked = Config.RdpRedirectDrive;
            RdpDrivePathBox.Text = Config.RdpDrivePath ?? "";
        }
        else
        {
            Config = new ServerConfig();

            if (!string.IsNullOrEmpty(defaultFolder) && FolderBox.Items.Contains(defaultFolder))
                FolderBox.SelectedItem = defaultFolder;
            else
                FolderBox.Text = defaultFolder ?? "";

            AuthMethodBox.SelectedIndex = 0;
            AppendRadio.IsChecked = true;
            AntiIdleCheck.IsChecked = false;
            AntiIdleStringBox.Text = "\\n";
            AntiIdleIntervalBox.Value = 60;

            StartupCmdCheck.IsChecked = false;

            LoginActionList.ItemsSource = LoginActions;
            PortForwardList.ItemsSource = PortForwardRules;

            // Appearance
            string targetPalette = "None";
            foreach (ComboBoxItem item in PaletteBox.Items)
            {
                if (item != null && item.Content?.ToString() == targetPalette)
                {
                    PaletteBox.SelectedItem = item;
                    break;
                }
            }
            if (PaletteBox.SelectedItem == null) PaletteBox.SelectedIndex = 0;

            SetComboValue(TermFontBox, "Cascadia Mono");
            SetComboValue(TermFontSizeBox, "16");

            TermFgBox.Text = "#D0D0D0";
            TermBgBox.Text = "#101010";
            TermScrollbackBox.Value = 10000;

            SyncSchemeDropdown(TermFgBox.Text, TermBgBox.Text);
            UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            RdpResBox.SelectedIndex = 0;
            RdpAudioCheck.IsChecked = true;
            RdpClipboardCheck.IsChecked = true;
            RdpCertCheck.IsChecked = true;
        }
    }

    public Task<bool> ShowModalAsync()
    {
        _tcs = new TaskCompletionSource<bool>();
        this.Closed += (s, e) => _tcs.TrySetResult(false);
        this.Activate();
        return _tcs.Task;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args?.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            TerminalPage.Visibility = tag == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
            LoginActionPage.Visibility = tag == "LoginAction" ? Visibility.Visible : Visibility.Collapsed;
            PortForwardingPage.Visibility = tag == "PortForwarding" ? Visibility.Visible : Visibility.Collapsed;
            AppearancePage.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            RdpPage.Visibility = tag == "RDP" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AuthMethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyFilePanel != null)
        {
            bool isKeyFile = (AuthMethodBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "key_file";
            KeyFilePanel.Visibility = isKeyFile ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file != null) KeyFileBox.Text = file.Path;
    }

    private async void BrowseLogPath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) LogPathTextBox.Text = folder.Path;
    }

    private async void BrowseStartupCmd_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".sh");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file != null) StartupCmdPathBox.Text = file.Path;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
        {
            return;
        }

        Config.Name = NameBox.Text;
        Config.Host = HostBox.Text;
        if (int.TryParse(PortBox.Text, out int p)) Config.Port = p;

        Config.Folder = FolderBox.SelectedItem?.ToString() ?? FolderBox.Text;
        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;
        Config.KeyFile = KeyFileBox.Text;

        if (AuthMethodBox.SelectedItem is ComboBoxItem item)
            Config.AuthMethod = item.Content?.ToString() ?? "password";

        // Terminal Logging
        Config.LoggingEnabled = EnableLoggingSwitch.IsOn;
        Config.LogPath = LogPathTextBox.Text;
        Config.LogMode = AppendRadio.IsChecked == true ? "append" : "overwrite";
        Config.AppendDataToLog = AppendDataCheck.IsChecked == true;
        Config.LogConnectString = LogConnectBox.Text;
        Config.LogDisconnectString = LogDisconnectBox.Text;
        Config.LogEachLineString = LogEachLineBox.Text;

        // Terminal Anti-idle
        Config.AntiIdleEnabled = AntiIdleCheck.IsChecked == true;
        Config.AntiIdleString = AntiIdleStringBox.Text;
        Config.AntiIdleInterval = double.IsNaN(AntiIdleIntervalBox.Value) ? 60 : (int)AntiIdleIntervalBox.Value;

        // Terminal Startup Command
        Config.StartupCmdEnabled = StartupCmdCheck.IsChecked == true;
        Config.StartupCmdPath = StartupCmdPathBox.Text;

        // Login Actions
        Config.LoginActions = new List<LoginActionStep>(LoginActions);

        // Appearance
        string finalFont = GetComboValue(TermFontBox, "Consolas");
        string finalSize = GetComboValue(TermFontSizeBox, "16");
        Config.TermFont = $"{finalFont.Trim()} {finalSize.Trim()}";
        Config.TermForeground = TermFgBox.Text;
        Config.TermBackground = TermBgBox.Text;
        Config.TermScrollback = double.IsNaN(TermScrollbackBox.Value) ? 10000 : (int)TermScrollbackBox.Value;
        Config.TermPalette = (PaletteBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";

        // Port Forward
        Config.PortForwardRules = new List<PortForwardRule>(PortForwardRules);

        // RDP
        Config.RdpEnabled = RdpEnabledSwitch.IsOn;
        if (int.TryParse(RdpPortBox.Text, out int rdpPort)) Config.RdpPort = rdpPort;
        Config.RdpResolution = (RdpResBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Full Screen";
        Config.RdpAudio = RdpAudioCheck.IsChecked == true;
        Config.RdpClipboard = RdpClipboardCheck.IsChecked == true;
        Config.RdpIgnoreCert = RdpCertCheck.IsChecked == true;
        Config.RdpRedirectDrive = RdpDriveCheck.IsChecked == true;
        Config.RdpDrivePath = RdpDrivePathBox.Text;

        Saved = true;
        _tcs?.TrySetResult(true);
        this.Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _tcs?.TrySetResult(false);
        this.Close();
    }

    // --- POPUP DIALOG LOGIN ACTIONS HANDLERS ---

    private async void LoginAction_Add_Click(object sender, RoutedEventArgs e)
    {
        var newStep = new LoginActionStep { Expect = "", Send = "", Timeout = 5 };
        if (await ShowLoginActionEditorAsync("Add Sequence Step", newStep))
        {
            LoginActions.Add(newStep);
        }
    }

    private async void LoginAction_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (LoginActionList.SelectedItem is not LoginActionStep selectedStep)
        {
            await ShowAlertAsync("Selection", "Please select a step to edit.");
            return;
        }

        if (await ShowLoginActionEditorAsync("Edit Sequence Step", selectedStep))
        {
            int idx = LoginActions.IndexOf(selectedStep);
            if (idx >= 0) LoginActions[idx] = selectedStep;
        }
    }

    private void LoginAction_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LoginActionList.SelectedItem is LoginActionStep step) LoginActions.Remove(step);
    }

    private void LoginAction_Up_Click(object sender, RoutedEventArgs e)
    {
        int idx = LoginActionList.SelectedIndex;
        if (idx > 0)
        {
            var item = LoginActions[idx];
            LoginActions.RemoveAt(idx);
            LoginActions.Insert(idx - 1, item);
            LoginActionList.SelectedIndex = idx - 1;
        }
    }

    private void LoginAction_Down_Click(object sender, RoutedEventArgs e)
    {
        int idx = LoginActionList.SelectedIndex;
        if (idx >= 0 && idx < LoginActions.Count - 1)
        {
            var item = LoginActions[idx];
            LoginActions.RemoveAt(idx);
            LoginActions.Insert(idx + 1, item);
            LoginActionList.SelectedIndex = idx + 1;
        }
    }

    private async Task<bool> ShowLoginActionEditorAsync(string title, LoginActionStep step)
    {
        var expectBox = new TextBox { Text = step.Expect, Header = "Expect (Pattern to match)", PlaceholderText = "e.g., password:" };
        var sendBox = new TextBox { Text = step.Send, Header = "Send (Response to send)", PlaceholderText = "e.g., MyPassword" };
        var timeoutBox = new NumberBox { Value = step.Timeout, Header = "Timeout (seconds)", Minimum = 1, Maximum = 300, HorizontalAlignment = HorizontalAlignment.Stretch };

        var contentPanel = new StackPanel
        {
            Spacing = 10,
            Width = 320,
            Children =
            {
                expectBox,
                sendBox,
                timeoutBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = contentPanel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            step.Expect = expectBox.Text.Trim();
            step.Send = sendBox.Text; // Keeping original formatting for passwords or spaces
            step.Timeout = double.IsNaN(timeoutBox.Value) ? 5 : (int)timeoutBox.Value;
            return true;
        }
        return false;
    }

    // --- OTHER UI HANDLERS ---

    private void ShowPasswordCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (PassBox != null)
        {
            PassBox.PasswordRevealMode = ShowPasswordCheck.IsChecked == true
                ? PasswordRevealMode.Visible
                : PasswordRevealMode.Hidden;
        }
    }

    private void ColorSchemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isColorUpdating) return;

        if (ColorSchemeBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            _isColorUpdating = true;
            string scheme = item.Content.ToString() ?? "";

            switch (scheme)
            {
                case "Black on light yellow":
                    TermFgBox.Text = "#000000"; TermBgBox.Text = "#FFFFDD"; break;
                case "Black on white":
                    TermFgBox.Text = "#000000"; TermBgBox.Text = "#FFFFFF"; break;
                case "Gray on black":
                    TermFgBox.Text = "#AAAAAA"; TermBgBox.Text = "#000000"; break;
                case "Green on black":
                    TermFgBox.Text = "#00FF00"; TermBgBox.Text = "#000000"; break;
                case "White on black":
                    TermFgBox.Text = "#FFFFFF"; TermBgBox.Text = "#000000"; break;
            }

            UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            _isColorUpdating = false;
        }
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            if (tb.Equals(TermFgBox)) UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            if (tb.Equals(TermBgBox)) UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            if (!_isColorUpdating && ColorSchemeBox.SelectedIndex != 5 && tb.FocusState != FocusState.Unfocused)
            {
                _isColorUpdating = true;
                ColorSchemeBox.SelectedIndex = 5;
                _isColorUpdating = false;
            }
        }
    }

    private void UpdateColorPreview(string hex, Microsoft.UI.Xaml.Shapes.Rectangle preview)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith("#"))
            {
                hex = hex.TrimStart('#');
                byte a = 255;
                int offset = 0;

                if (hex.Length == 8)
                {
                    a = Convert.ToByte(hex.Substring(0, 2), 16);
                    offset = 2;
                }

                if (hex.Length == 6 || hex.Length == 8)
                {
                    byte r = Convert.ToByte(hex.Substring(offset, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);

                    preview.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
                }
            }
        }
        catch { }
    }

    private void TermFgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        TermFgBox.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        if (!_isColorUpdating) ColorSchemeBox.SelectedIndex = 5;
    }

    private void TermBgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        TermBgBox.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        if (!_isColorUpdating) ColorSchemeBox.SelectedIndex = 5;
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        var localSettings = new Services.UnpackagedSettings();

        _isColorUpdating = true;

        PaletteBox.SelectedIndex = (int)(localSettings.Values["GlobalDefaultPalette"] ?? 0);
        ColorSchemeBox.SelectedIndex = (int)(localSettings.Values["GlobalDefaultScheme"] ?? 0);

        TermFgBox.Text = localSettings.Values["GlobalDefaultFg"] as string ?? "#000000";
        TermBgBox.Text = localSettings.Values["GlobalDefaultBg"] as string ?? "#FFFFDD";

        string globalFont = localSettings.Values["GlobalDefaultFont"] as string ?? "Consolas 16";
        int lastSpaceGlobal = globalFont.LastIndexOf(' ');
        string globalFontName = lastSpaceGlobal > 0 ? globalFont.Substring(0, lastSpaceGlobal).Trim() : globalFont;
        double globalFontSize = 16;

        if (lastSpaceGlobal > 0 && double.TryParse(globalFont.Substring(lastSpaceGlobal + 1), out double parsedGlobalSize))
        {
            globalFontSize = parsedGlobalSize;
        }

        SetComboValue(TermFontBox, globalFontName);
        SetComboValue(TermFontSizeBox, globalFontSize.ToString());

        TermScrollbackBox.Value = (double)(localSettings.Values["GlobalDefaultScrollback"] ?? 10000.0);

        string? globalLog = localSettings.Values["GlobalDefaultLogPath"] as string;
        if (!string.IsNullOrWhiteSpace(globalLog))
        {
            LogPathTextBox.Text = globalLog;
        }

        _isColorUpdating = false;

        UpdateColorPreview(TermFgBox.Text, TermFgPreview);
        UpdateColorPreview(TermBgBox.Text, TermBgPreview);
    }

    private void SyncSchemeDropdown(string fg, string bg)
    {
        _isColorUpdating = true;
        string fgUpper = fg?.ToUpper() ?? "";
        string bgUpper = bg?.ToUpper() ?? "";

        if (fgUpper == "#000000" && bgUpper == "#FFFFDD") ColorSchemeBox.SelectedIndex = 0;
        else if (fgUpper == "#000000" && bgUpper == "#FFFFFF") ColorSchemeBox.SelectedIndex = 1;
        else if (fgUpper == "#AAAAAA" && bgUpper == "#000000") ColorSchemeBox.SelectedIndex = 2;
        else if (fgUpper == "#00FF00" && bgUpper == "#000000") ColorSchemeBox.SelectedIndex = 3;
        else if (fgUpper == "#FFFFFF" && bgUpper == "#000000") ColorSchemeBox.SelectedIndex = 4;
        else ColorSchemeBox.SelectedIndex = 5;
        _isColorUpdating = false;
    }

    // --- POPUP DIALOG PORT FORWARDING HANDLERS ---

    private async void PortForward_Add_Click(object sender, RoutedEventArgs e)
    {
        var newRule = new PortForwardRule { Name = "", Type = "Local", SourcePort = 8080, DestinationHost = "localhost", DestinationPort = 80 };
        if (await ShowPortForwardEditorAsync("Add Port Forwarding Rule", newRule))
        {
            PortForwardRules.Add(newRule);
        }
    }

    private async void PortForward_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardList.SelectedItem is not PortForwardRule selectedRule)
        {
            await ShowAlertAsync("Selection", "Please select a rule to edit.");
            return;
        }

        if (await ShowPortForwardEditorAsync("Edit Port Forwarding Rule", selectedRule))
        {
            int idx = PortForwardRules.IndexOf(selectedRule);
            if (idx >= 0) PortForwardRules[idx] = selectedRule;
        }
    }

    private void PortForward_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardList.SelectedItem is PortForwardRule rule)
        {
            PortForwardRules.Remove(rule);
        }
    }

    private async Task<bool> ShowPortForwardEditorAsync(string title, PortForwardRule rule)
    {
        var nameBox = new TextBox { Text = rule.Name, Header = "Rule Name (Optional)", PlaceholderText = "e.g., Web UI or DB Tunnel" };
        var typeBox = new ComboBox
        {
            ItemsSource = new[] { "Local", "Remote", "Dynamic" },
            SelectedItem = rule.Type,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var sourcePortBox = new TextBox { Text = rule.SourcePort.ToString(), Header = "Source Port" };
        var destHostBox = new TextBox { Text = rule.DestinationHost, Header = "Destination Host" };
        var destPortBox = new TextBox { Text = rule.DestinationPort.ToString(), Header = "Destination Port" };

        var contentPanel = new StackPanel
        {
            Spacing = 10,
            Width = 300,
            Children =
            {
                nameBox,
                new TextBlock { Text = "Type" },
                typeBox,
                sourcePortBox,
                destHostBox,
                destPortBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = contentPanel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            rule.Name = nameBox.Text.Trim();
            rule.Type = typeBox.SelectedItem?.ToString() ?? "Local";
            if (int.TryParse(sourcePortBox.Text, out int sp)) rule.SourcePort = sp;
            rule.DestinationHost = destHostBox.Text.Trim();
            if (int.TryParse(destPortBox.Text, out int dp)) rule.DestinationPort = dp;
            return true;
        }
        return false;
    }

    private async Task ShowAlertAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private string GetComboValue(ComboBox cb, string fallback)
    {
        if (cb.SelectedItem is ComboBoxItem item) return item.Content?.ToString() ?? fallback;
        if (cb.SelectedItem != null) return cb.SelectedItem.ToString() ?? fallback;
        if (!string.IsNullOrWhiteSpace(cb.Text)) return cb.Text;
        return fallback;
    }

    private void SetComboValue(ComboBox cb, string targetValue)
    {
        foreach (var item in cb.Items)
        {
            string itemStr = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
            if (itemStr == targetValue)
            {
                cb.SelectedItem = item;
                return;
            }
        }
        cb.Text = targetValue;
    }
}