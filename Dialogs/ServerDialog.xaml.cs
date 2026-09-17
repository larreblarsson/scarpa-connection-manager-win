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

        // Native WinUI 3 way to attach a dialog to the main window
        this.XamlRoot = root;

        foreach (var f in folders) FolderBox.Items.Add(f);

        if (existingConfig != null)
        {
            Config = existingConfig.Clone();
            NameBox.Text = Config.Name ?? "";
            HostBox.Text = Config.Host ?? "";
            PortBox.Text = Config.Port.ToString();
            FolderBox.Text = Config.Folder ?? "";
            UserBox.Text = Config.User ?? "";
            PassBox.Password = Config.Password ?? "";

            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item != null && item.Content?.ToString() == Config.AuthMethod)
                    AuthMethodBox.SelectedItem = item;
            }
        }
        else
        {
            Config = new ServerConfig();
            FolderBox.Text = defaultFolder ?? "";
            AuthMethodBox.SelectedIndex = 0;
        }

        NavView.SelectedItem = NavView.MenuItems[0];
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
            RdpPage.Visibility = tag == "RDP" ? Visibility.Visible : Visibility.Collapsed;
            PlaceholderPage.Visibility = (tag != "General" && tag != "RDP") ? Visibility.Visible : Visibility.Collapsed;
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
        Config.Folder = FolderBox.Text;
        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;

        if (AuthMethodBox.SelectedItem is ComboBoxItem item)
            Config.AuthMethod = item.Content?.ToString() ?? "password";

        Saved = true;
    }
}