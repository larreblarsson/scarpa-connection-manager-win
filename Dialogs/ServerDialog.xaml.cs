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
    private LoginActionStep? _editingAction = null;

    public ServerConfig Config { get; private set; }
    public bool Saved { get; private set; } = false;

    private bool _isColorUpdating = false;
    private TaskCompletionSource<bool>? _tcs;

    public ObservableCollection<PortForwardRule> PortForwardRules { get; set; } = new();
    private PortForwardRule? _editingPortRule = null;

    // Note: XamlRoot was removed from the parameters since it's a standalone window now
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
            string fontStr = Config.TermFont ?? "Consolas 11";
            int lastSpace = fontStr.LastIndexOf(' ');
            string fontName;
            double fontSize;

            if (lastSpace > 0 && double.TryParse(fontStr.Substring(lastSpace + 1), out double parsedSize))
            {
                fontName = fontStr.Substring(0, lastSpace).Trim();
                fontSize = parsedSize;
            }
            else
            {
                fontName = fontStr;
                fontSize = 11;
            }

            // FIX: Try to set SelectedItem first. This forces WinUI to show the text!
            if (TermFontBox.Items.Contains(fontName))
            {
                TermFontBox.SelectedItem = fontName;
            }
            else
            {
                TermFontBox.Text = fontName;
            }

            // Convert the numeric size to a string and apply the same ComboBox fix
            string sizeStr = fontSize.ToString(); // Note: change 'fontSize' to 'globalFontSize' or 'parsedSize' depending on the method you are pasting into
            if (TermFontSizeBox.Items.Contains(sizeStr))
            {
                TermFontSizeBox.SelectedItem = sizeStr;
            }
            else
            {
                TermFontSizeBox.Text = sizeStr;
            }

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

            // 1. Initialize Default Palette
            string targetPalette = "None";
            foreach (ComboBoxItem item in PaletteBox.Items)
            {
                if (item != null && item.Content?.ToString() == targetPalette)
                {
                    PaletteBox.SelectedItem = item;
                    break;
                }
            }
            if (PaletteBox.SelectedItem == null) PaletteBox.SelectedIndex = 0; // Fallback

            // 2. Initialize Default Font
            if (TermFontBox.Items.Contains("Cascadia Mono")) TermFontBox.SelectedItem = "Cascadia Mono";
            else TermFontBox.Text = "Cascadia Mono";

            if (TermFontSizeBox.Items.Contains("11")) TermFontSizeBox.SelectedItem = "11";
            else TermFontSizeBox.Text = "11";

            TermFgBox.Text = "#D0D0D0";
            TermBgBox.Text = "#101010";
            TermScrollbackBox.Value = 10000;

            SyncSchemeDropdown(TermFgBox.Text, TermBgBox.Text);

            // Force the color bars to paint themselves when the window opens!
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
        this.Closed += (s, e) => _tcs.TrySetResult(false); // Fired if the user clicks the native 'X' close button
        this.Activate(); // Opens the window
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
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); // Now uses this window's handle
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

        //Login Actions
        Config.LoginActions = new List<LoginActionStep>(LoginActions);

        // Appearance
        Config.TermFont = $"{(TermFontBox.Text ?? "").Trim()} {(TermFontSizeBox.Text ?? "").Trim()}";
        Config.TermForeground = TermFgBox.Text;
        Config.TermBackground = TermBgBox.Text;
        Config.TermScrollback = double.IsNaN(TermScrollbackBox.Value) ? 10000 : (int)TermScrollbackBox.Value;
        Config.TermPalette = (PaletteBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";

        //Port Forward
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
        _tcs.TrySetResult(true);
        this.Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(false);
        this.Close();
    }

    private void LoginAction_Add_Click(object sender, RoutedEventArgs e)
    {
        _editingAction = null;
        LoginActionEditTitle.Text = "Add Sequence Step";
        LoginActionExpectBox.Text = "";
        LoginActionSendBox.Text = "";
        LoginActionTimeoutBox.Value = 5;
        LoginActionEditPanel.Visibility = Visibility.Visible;
    }

    private void LoginAction_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (LoginActionList.SelectedItem is LoginActionStep step)
        {
            _editingAction = step;
            LoginActionEditTitle.Text = "Edit Sequence Step";
            LoginActionExpectBox.Text = step.Expect;
            LoginActionSendBox.Text = step.Send;
            LoginActionTimeoutBox.Value = step.Timeout;
            LoginActionEditPanel.Visibility = Visibility.Visible;
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

    private void LoginAction_SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editingAction != null)
        {
            _editingAction.Expect = LoginActionExpectBox.Text;
            _editingAction.Send = LoginActionSendBox.Text;
            _editingAction.Timeout = double.IsNaN(LoginActionTimeoutBox.Value) ? 5 : (int)LoginActionTimeoutBox.Value;

            // Replace item to trigger UI refresh
            int idx = LoginActions.IndexOf(_editingAction);
            if (idx >= 0) LoginActions[idx] = _editingAction;
        }
        else
        {
            LoginActions.Add(new LoginActionStep
            {
                Expect = LoginActionExpectBox.Text,
                Send = LoginActionSendBox.Text,
                Timeout = double.IsNaN(LoginActionTimeoutBox.Value) ? 5 : (int)LoginActionTimeoutBox.Value
            });
        }
        LoginActionEditPanel.Visibility = Visibility.Collapsed;
    }

    private void LoginAction_CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        LoginActionEditPanel.Visibility = Visibility.Collapsed;
    }

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

            // CRITICAL FIX: Force the color bars to redraw instantly, bypassing the hidden text boxes
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

                // Support both #RRGGBB and #AARRGGBB hex formats
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
        catch { } // Silently ignore malformed colors
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
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        // Mute events so the Scheme dropdown doesn't scramble while loading
        _isColorUpdating = true;

        // 1. Apply ALL Global Appearance Defaults
        PaletteBox.SelectedIndex = (int)(localSettings.Values["GlobalDefaultPalette"] ?? 0);
        ColorSchemeBox.SelectedIndex = (int)(localSettings.Values["GlobalDefaultScheme"] ?? 0);

        TermFgBox.Text = localSettings.Values["GlobalDefaultFg"] as string ?? "#000000";
        TermBgBox.Text = localSettings.Values["GlobalDefaultBg"] as string ?? "#FFFFDD";

        // Parse the Global Font into Name and Size
        string globalFont = localSettings.Values["GlobalDefaultFont"] as string ?? "Consolas 11";
        int lastSpaceGlobal = globalFont.LastIndexOf(' ');
        string globalFontName;
        double globalFontSize;

        if (lastSpaceGlobal > 0 && double.TryParse(globalFont.Substring(lastSpaceGlobal + 1), out double parsedGlobalSize))
        {
            globalFontName = globalFont.Substring(0, lastSpaceGlobal).Trim();
            globalFontSize = parsedGlobalSize;
        }
        else
        {
            globalFontName = globalFont;
            globalFontSize = 11;
        }

        if (TermFontBox.Items.Contains(globalFontName))
        {
            TermFontBox.SelectedItem = globalFontName;
        }
        else
        {
            TermFontBox.Text = globalFontName;
        }

        // Convert the numeric size to a string and apply the same ComboBox fix
        string sizeStr = globalFontSize.ToString();
        if (TermFontSizeBox.Items.Contains(sizeStr))
        {
            TermFontSizeBox.SelectedItem = sizeStr;
        }
        else
        {
            TermFontSizeBox.Text = sizeStr;
        }

        // 2. Apply Global Terminal Defaults
        TermScrollbackBox.Value = (double)(localSettings.Values["GlobalDefaultScrollback"] ?? 10000.0);

        string? globalLog = localSettings.Values["GlobalDefaultLogPath"] as string;
        if (!string.IsNullOrWhiteSpace(globalLog))
        {
            LogPathTextBox.Text = globalLog; // Safely set the log path if one exists globally
        }

        _isColorUpdating = false;

        // Force preview squares to update
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
        else ColorSchemeBox.SelectedIndex = 5; // Custom
        _isColorUpdating = false;
    }

    private void PortForward_Add_Click(object sender, RoutedEventArgs e)
    {
        _editingPortRule = null;
        PortForwardEditTitle.Text = "Add Port Forwarding Rule";
        PortForwardTypeBox.SelectedIndex = 0;
        PortForwardSourceBox.Value = 8080;
        PortForwardDestHostBox.Text = "localhost";
        PortForwardDestPortBox.Value = 80;
        PortForwardEditPanel.Visibility = Visibility.Visible;
    }

    private void PortForward_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardList.SelectedItem is PortForwardRule rule)
        {
            _editingPortRule = rule;
            PortForwardEditTitle.Text = "Edit Port Forwarding Rule";

            foreach (ComboBoxItem item in PortForwardTypeBox.Items)
            {
                if (item.Content?.ToString() == rule.Type)
                    PortForwardTypeBox.SelectedItem = item;
            }

            PortForwardSourceBox.Value = rule.SourcePort;
            PortForwardDestHostBox.Text = rule.DestinationHost;
            PortForwardDestPortBox.Value = rule.DestinationPort;
            PortForwardEditPanel.Visibility = Visibility.Visible;
        }
    }

    private void PortForward_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardList.SelectedItem is PortForwardRule rule)
            PortForwardRules.Remove(rule);
    }

    private void PortForward_SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        string type = (PortForwardTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Local";
        int source = double.IsNaN(PortForwardSourceBox.Value) ? 8080 : (int)PortForwardSourceBox.Value;
        string destHost = PortForwardDestHostBox.Text.Trim();
        int destPort = double.IsNaN(PortForwardDestPortBox.Value) ? 80 : (int)PortForwardDestPortBox.Value;

        if (_editingPortRule != null)
        {
            _editingPortRule.Type = type;
            _editingPortRule.SourcePort = source;
            _editingPortRule.DestinationHost = destHost;
            _editingPortRule.DestinationPort = destPort;

            // Replace item to trigger UI refresh (same trick used in LoginActions)
            int idx = PortForwardRules.IndexOf(_editingPortRule);
            if (idx >= 0) PortForwardRules[idx] = _editingPortRule;
        }
        else
        {
            PortForwardRules.Add(new PortForwardRule
            {
                Type = type,
                SourcePort = source,
                DestinationHost = destHost,
                DestinationPort = destPort
            });
        }
        PortForwardEditPanel.Visibility = Visibility.Collapsed;
    }

    private void PortForward_CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        PortForwardEditPanel.Visibility = Visibility.Collapsed;
    }

    private void PortForwardTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortForwardDestHostBox == null) return;

        // Hide Destination fields if this is a Dynamic (SOCKS) proxy rule
        bool isDynamic = (PortForwardTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Dynamic";
        PortForwardDestHostBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
        PortForwardDestPortBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
    }
}