using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class ServerDialog : ContentDialog
{
    public ServerConfig Config { get; private set; }
    public bool Saved { get; private set; } = false;

    public ServerDialog(ServerConfig? existingConfig, IEnumerable<string> folders, XamlRoot root, string? defaultFolder = null)
    {
        this.InitializeComponent();
        this.XamlRoot = root;

        // FIX: Trigger the navigation selection immediately so the GeneralPage becomes visible.
        // This ensures the PasswordBox is fully loaded and won't clear itself when we assign the password.
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

            // Because the page is already visible, the PasswordBox will now retain this value.
            PassBox.Password = Config.Password ?? "";

            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item != null && item.Content?.ToString() == Config.AuthMethod)
                    AuthMethodBox.SelectedItem = item;
            }
            EnableLoggingSwitch.IsOn = Config.LoggingEnabled;
            LogFolderPathBox.Text = Config.LogPath ?? "";
            LogBehaviorBox.SelectedIndex = Config.LogMode == "append" ? 0 : 1;
        }
        else
        {
            Config = new ServerConfig();

            if (!string.IsNullOrEmpty(defaultFolder) && FolderBox.Items.Contains(defaultFolder))
                FolderBox.SelectedItem = defaultFolder;
            else
                FolderBox.Text = defaultFolder ?? "";

            AuthMethodBox.SelectedIndex = 0;
            LogBehaviorBox.SelectedIndex = 1;
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

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
        {
            args.Cancel = true; // Prevents the dialog from closing!
            return;
        }

        Config.Name = NameBox.Text;
        Config.Host = HostBox.Text;
        if (int.TryParse(PortBox.Text, out int p)) Config.Port = p;

        // Ensure we capture either the selected item or manually typed text
        Config.Folder = FolderBox.SelectedItem?.ToString() ?? FolderBox.Text;

        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;

        if (AuthMethodBox.SelectedItem is ComboBoxItem item)
            Config.AuthMethod = item.Content?.ToString() ?? "password";

        Config.LoggingEnabled = EnableLoggingSwitch.IsOn;
        Config.LogPath = LogFolderPathBox.Text;
        Config.LogMode = LogBehaviorBox.SelectedIndex == 0 ? "append" : "overwrite";

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