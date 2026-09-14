using Microsoft.UI.Xaml.Controls;
using ScarpaConnectionManager.Models;
using System.Collections.Generic;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class ServerDialog : ContentDialog
{
    public ServerConfig Config { get; private set; }

    public ServerDialog(ServerConfig? existingConfig, IEnumerable<string> folders, string? defaultFolder = null)
    {
        this.InitializeComponent();

        // Populate folder dropdown
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
            RdpEnabledCheck.IsChecked = Config.RdpEnabled;

            foreach (ComboBoxItem item in AuthMethodBox.Items)
            {
                if (item.Content.ToString() == Config.AuthMethod) AuthMethodBox.SelectedItem = item;
            }
        }
        else
        {
            Config = new ServerConfig();
            FolderBox.Text = defaultFolder ?? "";
            AuthMethodBox.SelectedIndex = 0; // Default to 'password'
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Simple validation
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
        {
            args.Cancel = true;
            return;
        }

        Config.Name = NameBox.Text;
        Config.Host = HostBox.Text;
        if (int.TryParse(PortBox.Text, out int p)) Config.Port = p;
        Config.Folder = FolderBox.Text;
        Config.User = UserBox.Text;
        Config.Password = PassBox.Password;
        Config.RdpEnabled = RdpEnabledCheck.IsChecked == true;

        if (AuthMethodBox.SelectedItem is ComboBoxItem item)
        {
            Config.AuthMethod = item.Content.ToString();
        }
    }
}
}
