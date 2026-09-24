using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // Required for GetActiveWindow

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class ServerDialog : ContentDialog
{
    // API call to get the main window handle (required for FilePicker in a ContentDialog)
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    public ServerConfig Config { get; private set; }
    public bool Saved { get; private set; } = false;

    public ServerDialog(ServerConfig? existingConfig, IEnumerable<string> folders, XamlRoot root, string? defaultFolder = null)
    {
        this.InitializeComponent();
        this.XamlRoot = root;

        NavView.SelectedItem = NavView.MenuItems[0];

        foreach (var f in folders) FolderBox.Items.Add(f);

        if (existingConfig != null)
        {
            Config = existingConfig.Clone();
            NameBox.Text = Config.Name ?? "";
            HostBox.Text = Config.Host ?? "";
            PortBox.Text = Config.Port.ToString();

            if (!string.IsNullOrEmpty(Config.Folder) && FolderBox.Items.Contains(Config.Folder))
                FolderBox.SelectedItem = Config.Folder;
            else
                FolderBox.Text = Config.Folder ?? "";

            UserBox.Text = Config.User ?? "";
            PassBox.Password = Config.Password ?? "";

            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item != null && item.Content?.ToString() == Config.AuthMethod)
                    AuthMethodBox.SelectedItem = item;
            }

            // Map Logging Settings
            EnableLoggingSwitch.IsOn = Config.LoggingEnabled;
            LogPathTextBox.Text = Config.LogPath ?? "";

            if (Config.LogMode == "overwrite") OverwriteRadio.IsChecked = true;
            else AppendRadio.IsChecked = true;

            // Load Append Data settings
            AppendDataCheck.IsChecked = Config.AppendDataToLog;
            LogConnectBox.Text = Config.LogConnectString ?? "";
            LogDisconnectBox.Text = Config.LogDisconnectString ?? "";
            LogEachLineBox.Text = Config.LogEachLineString ?? "";

            // Load Anti-idle settings
            AntiIdleCheck.IsChecked = Config.AntiIdleEnabled;
            AntiIdleStringBox.Text = Config.AntiIdleString ?? "";
            AntiIdleIntervalBox.Value = Config.AntiIdleInterval;
        }
        else
        {
            Config = new ServerConfig();

            if (!string.IsNullOrEmpty(defaultFolder) && FolderBox.Items.Contains(defaultFolder))
                FolderBox.SelectedItem = defaultFolder;
            else
                FolderBox.Text = defaultFolder ?? "";

            AuthMethodBox.SelectedIndex = 0;
            AppendRadio.IsChecked = true; // Default to append

            // Default Anti-idle settings for new servers
            AntiIdleCheck.IsChecked = false;
            AntiIdleStringBox.Text = "\\n";
            AntiIdleIntervalBox.Value = 60;
        }
    }

    public async Task<bool> ShowModalAsync()
    {
        var result = await this.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args?.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            TerminalPage.Visibility = tag == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
            RdpPage.Visibility = tag == "RDP" ? Visibility.Visible : Visibility.Collapsed;
            PlaceholderPage.Visibility = (tag != "General" && tag != "RDP" && tag != "Terminal") ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void BrowseLogPath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();

        // Bind the file picker to the active window
        IntPtr hwnd = GetActiveWindow();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text File", new List<string>() { ".txt", ".log" });
        picker.SuggestedFileName = "scarpa_session_log.txt";

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            LogPathTextBox.Text = file.Path;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
        {
            args.Cancel = true;
            return;
        }

        Config.Name = NameBox.Text;
        Config.Host = HostBox.Text;
        if (int.TryParse(PortBox.Text, out int p)) Config.Port = p;

        Config.Folder = FolderBox.SelectedItem?.ToString() ?? FolderBox.Text;
        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;

        if (AuthMethodBox.SelectedItem is ComboBoxItem item)
            Config.AuthMethod = item.Content?.ToString() ?? "password";

        // Save Logging Settings
        Config.LoggingEnabled = EnableLoggingSwitch.IsOn;
        Config.LogPath = LogPathTextBox.Text;
        Config.LogMode = AppendRadio.IsChecked == true ? "append" : "overwrite";

        // Save Append Data settings
        Config.AppendDataToLog = AppendDataCheck.IsChecked == true;
        Config.LogConnectString = LogConnectBox.Text;
        Config.LogDisconnectString = LogDisconnectBox.Text;
        Config.LogEachLineString = LogEachLineBox.Text;

        // Save Anti-idle settings
        Config.AntiIdleEnabled = AntiIdleCheck.IsChecked == true;
        Config.AntiIdleString = AntiIdleStringBox.Text;

        // NumberBox uses doubles, so we cast it safely back to an integer for the model
        Config.AntiIdleInterval = double.IsNaN(AntiIdleIntervalBox.Value) ? 60 : (int)AntiIdleIntervalBox.Value;

        Saved = true;
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
}